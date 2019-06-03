﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using ISAAR.MSolve.Discretization.Commons;
using ISAAR.MSolve.Discretization.FreedomDegrees;
using ISAAR.MSolve.Discretization.Interfaces;
using ISAAR.MSolve.LinearAlgebra.Iterative.PreconditionedConjugateGradient;
using ISAAR.MSolve.LinearAlgebra.Iterative.Termination;
using ISAAR.MSolve.LinearAlgebra.Matrices;
using ISAAR.MSolve.LinearAlgebra.Triangulation;
using ISAAR.MSolve.LinearAlgebra.Vectors;
using ISAAR.MSolve.Solvers.Assemblers;
using ISAAR.MSolve.Solvers.Commons;
using ISAAR.MSolve.Solvers.DomainDecomposition.Dual.FetiDP.CornerNodes;
using ISAAR.MSolve.Solvers.DomainDecomposition.Dual.FetiDP.InterfaceProblem;
using ISAAR.MSolve.Solvers.DomainDecomposition.Dual.LagrangeMultipliers;
using ISAAR.MSolve.Solvers.DomainDecomposition.Dual.Pcg;
using ISAAR.MSolve.Solvers.DomainDecomposition.Dual.Preconditioning;
using ISAAR.MSolve.Solvers.DomainDecomposition.Dual.StiffnessDistribution;
using ISAAR.MSolve.Solvers.LinearSystems;
using ISAAR.MSolve.Solvers.Ordering;
using ISAAR.MSolve.Solvers.Ordering.Reordering;

//TODO: Rigid body modes do not have to be computed each time the stiffness matrix changes. 
namespace ISAAR.MSolve.Solvers.DomainDecomposition.Dual.FetiDP
{
    public class FetiDPSolver : ISolver
    {
        internal const string name = "FETI-DP Solver"; // for error messages
        private readonly Dictionary<int, SkylineAssembler> assemblers;
        private readonly ICornerNodeSelection cornerNodeSelection;
        private readonly ICrosspointStrategy crosspointStrategy = new FullyRedundantConstraints();
        private readonly IDofOrderer dofOrderer;
        private readonly IFetiDPInterfaceProblemSolver interfaceProblemSolver;
        private readonly bool problemIsHomogeneous;
        private readonly Dictionary<int, SingleSubdomainSystem<SkylineMatrix>> linearSystems;
        private readonly IStructuralModel model;
        private readonly IFetiPreconditionerFactory preconditionerFactory;

        //TODO: fix the mess of Dictionary<int, ISubdomain>, List<ISubdomain>, Dictionary<int, Subdomain>, List<Subdomain>
        //      The concrete are useful for the preprocessor mostly, while analyzers, solvers need the interface versions.
        //      Lists are better in analyzers and solvers. Dictionaries may be better in the preprocessors.
        private readonly Dictionary<int, ISubdomain> subdomains;

        private FetiDPDofSeparator dofSeparator;
        private Dictionary<int, CholeskyFull> factorizedKrr; //TODO: CholeskySkyline or CholeskySuiteSparse
        private bool factorizeInPlace = true;
        private FetiDPFlexibilityMatrix flexibility;
        private bool isStiffnessModified = true;
        private FetiDPLagrangeMultipliersEnumerator lagrangeEnumerator;
        private IFetiPreconditioner preconditioner;
        private IStiffnessDistribution stiffnessDistribution;
        private FetiDPSubdomainGlobalMapping subdomainGlobalMapping;

        private FetiDPSolver(IStructuralModel model, ICornerNodeSelection cornerNodeSelection,
            IDofOrderer dofOrderer, IFetiPreconditionerFactory preconditionerFactory,
            IFetiDPInterfaceProblemSolver interfaceProblemSolver, bool problemIsHomogeneous)
        {
            // Model
            if (model.Subdomains.Count == 1) throw new InvalidSolverException(
                $"{name} cannot be used if there is only 1 subdomain");
            this.model = model;

            // Subdomains
            subdomains = new Dictionary<int, ISubdomain>();
            foreach (ISubdomain subdomain in model.Subdomains) subdomains[subdomain.ID] = subdomain;

            // Linear systems
            linearSystems = new Dictionary<int, SingleSubdomainSystem<SkylineMatrix>>();
            var tempLinearSystems = new Dictionary<int, ILinearSystem>();
            assemblers = new Dictionary<int, SkylineAssembler>();
            foreach (ISubdomain subdomain in model.Subdomains)
            {
                int id = subdomain.ID;
                var linearSystem = new SingleSubdomainSystem<SkylineMatrix>(subdomain);
                linearSystems.Add(id, linearSystem);
                tempLinearSystems.Add(id, linearSystem);
                linearSystem.MatrixObservers.Add(this);
                assemblers.Add(id, new SkylineAssembler());
            }
            LinearSystems = tempLinearSystems;

            this.cornerNodeSelection = cornerNodeSelection;
            this.dofOrderer = dofOrderer;
            this.preconditionerFactory = preconditionerFactory;

            // Interface problem
            this.interfaceProblemSolver = interfaceProblemSolver;

            // Homogeneous/heterogeneous problems
            this.problemIsHomogeneous = problemIsHomogeneous;
        }

        public Dictionary<int, HashSet<INode>> CornerNodesOfSubdomains { get; private set; }
        public IReadOnlyDictionary<int, ILinearSystem> LinearSystems { get; }
        public SolverLogger Logger { get; } = new SolverLogger(name);

        public Dictionary<int, IMatrix> BuildGlobalMatrices(IElementMatrixProvider elementMatrixProvider)
        {
            var watch = new Stopwatch();
            watch.Start();
            var matricesInternal = new Dictionary<int, IMatrixView>();
            var matricesResult = new Dictionary<int, IMatrix>();
            foreach (ISubdomain subdomain in model.Subdomains) //TODO: this must be done in parallel
            {
                SkylineMatrix matrix = assemblers[subdomain.ID].BuildGlobalMatrix(subdomain.FreeDofOrdering,
                    subdomain.Elements, elementMatrixProvider);
                matricesInternal[subdomain.ID] = matrix;
                matricesResult[subdomain.ID] = matrix;
            }
            watch.Stop();
            Logger.LogTaskDuration("Matrix assembly", watch.ElapsedMilliseconds);

            // Use the newly created stiffnesses to determine the stiffness distribution between subdomains.
            //TODO: Should this be done here or before factorizing by checking that isMatrixModified? 
            //TODO: This should probably be timed as well.
            DetermineStiffnessDistribution(matricesInternal);

            return matricesResult;
        }

        public Dictionary<int, (IMatrix matrixFreeFree, IMatrixView matrixFreeConstr, IMatrixView matrixConstrFree,
            IMatrixView matrixConstrConstr)> BuildGlobalSubmatrices(IElementMatrixProvider elementMatrixProvider)
        {
            var watch = new Stopwatch();
            watch.Start();
            var matricesResult = new Dictionary<int, (IMatrix Aff, IMatrixView Afc, IMatrixView Acf, IMatrixView Acc)>();
            var matricesInternal = new Dictionary<int, IMatrixView>();
            foreach (ISubdomain subdomain in model.Subdomains) //TODO: this must be done in parallel
            {
                if (subdomain.ConstrainedDofOrdering == null)
                {
                    throw new InvalidOperationException("In order to build the matrices corresponding to constrained dofs of,"
                        + $" subdomain {subdomain.ID}, they must have been ordered first.");
                }
                (SkylineMatrix Kff, IMatrixView Kfc, IMatrixView Kcf, IMatrixView Kcc) =
                    assemblers[subdomain.ID].BuildGlobalSubmatrices(subdomain.FreeDofOrdering,
                    subdomain.ConstrainedDofOrdering, subdomain.Elements, elementMatrixProvider);
                matricesResult[subdomain.ID] = (Kff, Kfc, Kcf, Kcc);
                matricesInternal[subdomain.ID] = Kff;
            }
            watch.Stop();
            Logger.LogTaskDuration("Matrix assembly", watch.ElapsedMilliseconds);

            // Use the newly created stiffnesses to determine the stiffness distribution between subdomains.
            //TODO: Should this be done here or before factorizing by checking that isMatrixModified? 
            //TODO: This should probably be timed as well.
            DetermineStiffnessDistribution(matricesInternal);

            return matricesResult;
        }

        //TODO: this and the fields should be handled by a class that handles dof mappings.
        public Dictionary<int, SparseVector> DistributeNodalLoads(Table<INode, IDofType, double> globalNodalLoads)
            => subdomainGlobalMapping.DistributeNodalLoads(subdomains, globalNodalLoads);

        //TODO: this and the fields should be handled by a class that handles dof mappings.
        public Vector GatherGlobalDisplacements(Dictionary<int, IVectorView> subdomainDisplacements)
            => subdomainGlobalMapping.GatherGlobalDisplacements(subdomainDisplacements);

        public void HandleMatrixWillBeSet()
        {
            isStiffnessModified = true;
            factorizedKrr = null;
            flexibility = null;
            preconditioner = null;
            interfaceProblemSolver.ClearCoarseProblemMatrix();

            //stiffnessDistribution = null; //WARNING: do not dispose of this. It is updated when BuildGlobalMatrix() is called.
        }

        public void Initialize()
        { }

        public Dictionary<int, Matrix> InverseSystemMatrixTimesOtherMatrix(Dictionary<int, IMatrixView> otherMatrix)
        {
            throw new NotImplementedException();
        }

        public void OrderDofs(bool alsoOrderConstrainedDofs)
        {
            var watch = new Stopwatch();
            watch.Start();

            // Order dofs
            IGlobalFreeDofOrdering globalOrdering = dofOrderer.OrderFreeDofs(model);
            model.GlobalDofOrdering = globalOrdering;
            foreach (ISubdomain subdomain in model.Subdomains)
            {
                assemblers[subdomain.ID].HandleDofOrderingWillBeModified();
                subdomain.FreeDofOrdering = globalOrdering.SubdomainDofOrderings[subdomain];
                if (alsoOrderConstrainedDofs) subdomain.ConstrainedDofOrdering = dofOrderer.OrderConstrainedDofs(subdomain);

                // The next must done by the analyzer, so that subdomain.Forces is retained when doing back to back analyses.
                //subdomain.Forces = linearSystem.CreateZeroVector();
            }

            // Identify corner nodes
            CornerNodesOfSubdomains = cornerNodeSelection.SelectCornerNodesOfSubdomains();

            // Define boundary / internal dofs
            dofSeparator = new FetiDPDofSeparator();
            dofSeparator.SeparateDofs(model, CornerNodesOfSubdomains);
            dofSeparator.DefineCornerMappingMatrices(model, CornerNodesOfSubdomains);

            // Define lagrange multipliers and boolean matrices
            this.lagrangeEnumerator = new FetiDPLagrangeMultipliersEnumerator(crosspointStrategy, dofSeparator);
            if (problemIsHomogeneous) lagrangeEnumerator.DefineBooleanMatrices(model); // optimization in this case
            else lagrangeEnumerator.DefineLagrangesAndBooleanMatrices(model);

            // Log dof statistics
            watch.Stop();
            Logger.LogTaskDuration("Dof ordering", watch.ElapsedMilliseconds);
            Logger.LogNumDofs("Global dofs", globalOrdering.NumGlobalFreeDofs);
            int numExpandedDomainFreeDofs = 0;
            foreach (var subdomain in model.Subdomains)
            {
                numExpandedDomainFreeDofs += subdomain.FreeDofOrdering.NumFreeDofs;
            }
            Logger.LogNumDofs("Expanded domain dofs", numExpandedDomainFreeDofs);
            Logger.LogNumDofs("Lagrange multipliers", lagrangeEnumerator.NumLagrangeMultipliers);
            Logger.LogNumDofs("Corner dofs", dofSeparator.NumGlobalCornerDofs);
        }

        public void PreventFromOverwrittingSystemMatrices() => factorizeInPlace = false;

        public void Solve()
        {
            var watch = new Stopwatch();
            foreach (var linearSystem in linearSystems.Values)
            {
                if (linearSystem.Solution == null) linearSystem.Solution = linearSystem.CreateZeroVector();
            }

            // Separate the force vector
            watch.Start();
            var fr = new Dictionary<int, Vector>();
            var fbc = new Dictionary<int, Vector>();
            foreach (int s in subdomains.Keys)
            {
                int[] remainderDofs = dofSeparator.RemainderDofIndices[s];
                int[] cornerDofs = dofSeparator.CornerDofIndices[s];
                Vector f = linearSystems[s].RhsVector;
                fr[s] = f.GetSubvector(remainderDofs);
                fbc[s] = f.GetSubvector(cornerDofs);
            }
            watch.Stop();
            Logger.LogTaskDuration("Separating vectors & matrices", watch.ElapsedMilliseconds);
            watch.Reset();

            // Separate the stiffness matrix
            Dictionary<int, Matrix> Krr = null; //TODO: perhaps SkylineMatrix or SymmetricCSC 
            Dictionary<int, Matrix> Krc = null; //TODO: perhaps CSR or CSC
            Dictionary<int, Matrix> Kcc = null; //TODO: perhaps SymmetricMatrix

            if (isStiffnessModified)
            {
                Krr = new Dictionary<int, Matrix>();  
                Krc = new Dictionary<int, Matrix>(); 
                Kcc = new Dictionary<int, Matrix>(); 
                factorizedKrr = new Dictionary<int, CholeskyFull>();

                // Separate the stiffness matrix
                watch.Start();
                foreach (int s in subdomains.Keys)
                {
                    int[] remainderDofs = dofSeparator.RemainderDofIndices[s];
                    int[] cornerDofs = dofSeparator.CornerDofIndices[s];
                    IMatrix Kff = linearSystems[s].Matrix;
                    Krr[s] = Kff.GetSubmatrix(remainderDofs, remainderDofs);
                    Krc[s] = Kff.GetSubmatrix(remainderDofs, cornerDofs);
                    Kcc[s] = Kff.GetSubmatrix(cornerDofs, cornerDofs);
                }
                watch.Stop();
                Logger.LogTaskDuration("Separating vectors & matrices", watch.ElapsedMilliseconds);

                // Create the preconditioner before overwriting Krr with its factorization.
                watch.Restart();
                BuildPreconditioner(Krr);
                watch.Stop();
                Logger.LogTaskDuration("Calculating preconditioner", watch.ElapsedMilliseconds);

                // Factorize matrices
                watch.Restart();
                foreach (int id in subdomains.Keys) factorizedKrr[id] = Krr[id].FactorCholesky(true);
                watch.Stop();
                Logger.LogTaskDuration("Matrix factorization", watch.ElapsedMilliseconds);

                // Define FETI-DP flexibility matrices
                watch.Restart();
                flexibility = new FetiDPFlexibilityMatrix(factorizedKrr, Krc, lagrangeEnumerator, dofSeparator);

                // Static condensation of remainder dofs (Schur complement).
                interfaceProblemSolver.CreateCoarseProblemMatrix(dofSeparator, factorizedKrr, Krc, Kcc);
                watch.Stop();
                Logger.LogTaskDuration("Setting up interface problem", watch.ElapsedMilliseconds);
                watch.Reset();

                isStiffnessModified = false;
            }

            // Static condensation for the force vectors
            watch.Start();
            Vector globalFcStar = interfaceProblemSolver.CreateCoarseProblemRhs(dofSeparator, factorizedKrr, Krc, fr, fbc);

            // Calculate the rhs vectors of the interface system
            Vector dr = CalcDisconnectedDisplacements(factorizedKrr, fr);
            double globalForcesNorm = CalcGlobalForcesNorm();
            watch.Stop();
            Logger.LogTaskDuration("Setting up interface problem", watch.ElapsedMilliseconds);

            // Solve the interface problem
            watch.Restart();
            (Vector lagranges, Vector uc) = interfaceProblemSolver.SolveInterfaceProblem(flexibility, preconditioner, 
                globalFcStar, dr, globalForcesNorm, Logger);
            watch.Stop();
            Logger.LogTaskDuration("Solving interface problem", watch.ElapsedMilliseconds);

            // Calculate the displacements of each subdomain
            watch.Restart();
            Dictionary<int, Vector> actualDisplacements = CalcActualDisplacements(lagranges, uc, Krc, fr);
            foreach (var idSystem in linearSystems) idSystem.Value.Solution = actualDisplacements[idSystem.Key];
            watch.Stop();
            Logger.LogTaskDuration("Calculate displacements from lagrange multipliers", watch.ElapsedMilliseconds);

            Logger.IncrementAnalysisStep();
        }

        /// <summary>
        /// Does not mutate this object.
        /// </summary>
        internal Dictionary<int, Vector> CalcActualDisplacements(Vector lagranges, Vector cornerDisplacements,
            Dictionary<int, Matrix> Krc, Dictionary<int, Vector> fr)
        {
            var freeDisplacements = new Dictionary<int, Vector>();
            foreach (int s in subdomains.Keys)
            {
                // ur[s] = inv(Krr[s]) * (fr[s] - Br[s]^T * lagranges - Krc[s] * Lc[s] * uc)
                Vector BrLambda = lagrangeEnumerator.BooleanMatrices[s].Multiply(lagranges, true);
                Vector KrcLcUc = dofSeparator.CornerBooleanMatrices[s].Multiply(cornerDisplacements);
                KrcLcUc = Krc[s].Multiply(KrcLcUc);
                Vector temp = fr[s].Copy();
                temp.SubtractIntoThis(BrLambda);
                temp.SubtractIntoThis(KrcLcUc);
                Vector ur = factorizedKrr[s].SolveLinearSystem(temp);

                // uf[s] = union(ur[s], ubc[s])
                // Remainder dofs
                var uf = Vector.CreateZero(subdomains[s].FreeDofOrdering.NumFreeDofs);
                int[] remainderDofs = dofSeparator.RemainderDofIndices[s];
                uf.CopyNonContiguouslyFrom(remainderDofs, ur);

                // Corner dofs: ubc[s] = Bc[s] * uc
                Vector ubc = dofSeparator.CornerBooleanMatrices[s].Multiply(cornerDisplacements);
                int[] cornerDofs = dofSeparator.CornerDofIndices[s];
                uf.CopyNonContiguouslyFrom(cornerDofs, ubc);

                freeDisplacements[s] = uf;
            }
            return freeDisplacements;
        }

        /// <summary>
        /// d = sum(Bs * generalInverse(Ks) * fs), where fs are the nodal forces applied to the dofs of subdomain s.
        /// Does not mutate this object.
        /// </summary>
        internal Vector CalcDisconnectedDisplacements(Dictionary<int, CholeskyFull> factorizedKrr, 
            Dictionary<int, Vector> fr)
        {
            // dr = sum_over_s( Br[s] * inv(Krr[s]) * fr[s])
            var dr = Vector.CreateZero(lagrangeEnumerator.NumLagrangeMultipliers);
            foreach (int s in linearSystems.Keys)
            {
                SignedBooleanMatrix Br = lagrangeEnumerator.BooleanMatrices[s];
                dr.AddIntoThis(Br.Multiply(factorizedKrr[s].SolveLinearSystem(fr[s])));
            }
            return dr;
        }

        private void BuildPreconditioner(Dictionary<int, Matrix> matricesKrr)
        {
            // Create the preconditioner. 
            //TODO: this should be done simultaneously with the factorizations to avoid duplicate factorizations.
            var stiffnessMatrices = new Dictionary<int, IMatrixView>();
            foreach (var idKrr in matricesKrr) stiffnessMatrices.Add(idKrr.Key, idKrr.Value);
            preconditioner = preconditionerFactory.CreatePreconditioner(stiffnessDistribution, dofSeparator,
                lagrangeEnumerator, stiffnessMatrices);
        }

        /// <summary>
        /// Calculate the norm of the forces vector |f| = |K*u|. It is needed to check the convergence of PCG/PCPG.
        /// </summary>
        private double CalcGlobalForcesNorm()
        {
            //TODO: It would be better to do that using the global vector to avoid the homogeneous/heterogeneous averaging
            //      That would require the analyzer to build the global vector too though. Caution: we cannot take just 
            //      the nodal loads from the model, since the effect of other loads is only taken into account int 
            //      linearSystem.Rhs. Even if we could easily create the global forces vector, it might be wrong since 
            //      the analyzer may modify some of these loads, depending on time step, loading step, etc.
            var subdomainForces = new Dictionary<int, IVectorView>();
            foreach (var linearSystem in linearSystems.Values)
            {
                subdomainForces[linearSystem.Subdomain.ID] = linearSystem.RhsVector;
            }
            return subdomainGlobalMapping.CalculateGlobalForcesNorm(subdomainForces);
        }

        private void DetermineStiffnessDistribution(Dictionary<int, IMatrixView> stiffnessMatrices)
        {
            // Use the newly created stiffnesses to determine the stiffness distribution between subdomains.
            //TODO: Should this be done here or before factorizing by checking that isMatrixModified? 
            if (problemIsHomogeneous)
            {
                stiffnessDistribution = new HomogeneousStiffnessDistribution(model, dofSeparator);
            }
            else
            {
                Table<INode, IDofType, BoundaryDofLumpedStiffness> boundaryDofStiffnesses = 
                    BoundaryDofLumpedStiffness.ExtractBoundaryDofLumpedStiffnesses(
                        dofSeparator.GlobalBoundaryDofs, stiffnessMatrices);
                stiffnessDistribution = new HeterogeneousStiffnessDistribution(model, dofSeparator, boundaryDofStiffnesses);
            }
            subdomainGlobalMapping = new FetiDPSubdomainGlobalMapping(model, dofSeparator, stiffnessDistribution);
        }

        public class Builder
        {
            private ICornerNodeSelection cornerNodeSelection; //TODO: These should probably be a HashSet instead of array.

            public Builder(ICornerNodeSelection cornerNodeSelection)
            {
                this.cornerNodeSelection = cornerNodeSelection;
            }

            //TODO: We need to specify the ordering for remainder and possibly internal dofs, while IDofOrderer only works for free dofs.
            public IDofOrderer DofOrderer { get; set; } =
                new DofOrderer(new NodeMajorDofOrderingStrategy(), new NullReordering());

            public IFetiDPInterfaceProblemSolver InterfaceProblemSolver { get; set; } =
                new FetiDPInterfaceProblemSolver.Builder().Build();

            public IFetiPreconditionerFactory PreconditionerFactory { get; set; } = new LumpedPreconditioner.Factory();
            public bool ProblemIsHomogeneous { get; set; } = true;

            public FetiDPSolver BuildSolver(IStructuralModel model)
                => new FetiDPSolver(model, cornerNodeSelection, DofOrderer, PreconditionerFactory, 
                     InterfaceProblemSolver, ProblemIsHomogeneous);
        }
    }
}
