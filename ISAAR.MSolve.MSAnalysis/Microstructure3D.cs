﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ISAAR.MSolve.Materials.Interfaces; //using ISAAR.MSolve.PreProcessor.Interfaces;
using ISAAR.MSolve.Analyzers;
using ISAAR.MSolve.Logging;
using ISAAR.MSolve.PreProcessor;
using ISAAR.MSolve.Problems;
using ISAAR.MSolve.Solvers.Skyline;
using ISAAR.MSolve.LinearAlgebra.Vectors;
using ISAAR.MSolve.LinearAlgebra;
using ISAAR.MSolve.LinearAlgebra.Matrices;
using ISAAR.MSolve.FEM;
using ISAAR.MSolve.FEM.Elements;
using ISAAR.MSolve.FEM.Entities;
using ISAAR.MSolve.FEM.Materials;
using ISAAR.MSolve.Materials;
using ISAAR.MSolve.Solvers.Interfaces;
using ISAAR.MSolve.MultiscaleAnalysis.Interfaces;
using ISAAR.MSolve.FEM.Interfaces;
using ISAAR.MSolve.FEM.Providers;
using ISAAR.MSolve.MultiscaleAnalysis.SupportiveClasses;
using ISAAR.MSolve.Discretization.Interfaces;
using ISAAR.MSolve.Discretization.Providers;
using ISAAR.MSolve.Solvers.Ordering;
using ISAAR.MSolve.Solvers.Ordering.Reordering;
using ISAAR.MSolve.Solvers;
using ISAAR.MSolve.Solvers.LinearSystems;

namespace ISAAR.MSolve.MultiscaleAnalysis
{
    /// <summary>
    /// Primary multiscale analysis class that connects all nesessary structures for a 3D FE2 simulation
    /// Authors: Gerasimos Sotiropoulos
    /// </summary>
    public class Microstructure3D : StructuralProblemsMicrostructureBase_v2, IContinuumMaterial3D_v2
    {
        private Model_v2 model { get; set; }
        //private readonly Dictionary<int, Node> nodesDictionary = new Dictionary<int, Node>();
        private Dictionary<int, Node_v2> boundaryNodes { get; set; }
        Dictionary<int, Dictionary<int, Element_v2>> boundaryElements;
        private IRVEbuilder_v2 rveBuilder;
        private bool EstimateOnlyLinearResponse;
        //private NewtonRaphsonNonLinearAnalyzer microAnalyzer;
        private double volume;
        public Dictionary<int, IVector> uInitialFreeDOFDisplacementsPerSubdomain { get; private set; }
        Dictionary<int, Dictionary<DOFType, double>> initialConvergedBoundaryDisplacements;
        private IScaleTransitions_v2 scaleTransitions = new SmallStrain3DScaleTransition();
        Random rnd1 = new Random();
        ISolverBuilder_v2 solverBuilder;

        // aparaithta gia to implementation tou IFiniteElementMaterial3D
        Matrix constitutiveMatrix;
        private double[] trueStressVec = new double[6];
        private bool modified; // opws sto MohrCoulomb gia to modified

        private double[,] Cijrs_prev;
        private bool matrices_not_initialized = true;
        private double tol;
        public void InitializeMatrices()
        {
            Cijrs_prev = new double[6,6];
            matrices_not_initialized = false;
            tol = Math.Pow(10, -19);
            constitutiveMatrix = Matrix.CreateZero(6,6);
        }


        //double[] Stresses { get; }
        //IMatrix2D ConstitutiveMatrix { get; } TODOGerasimos

        //Random properties 
        private int database_size;

        public Microstructure3D(IRVEbuilder_v2 rveBuilder, ISolverBuilder_v2 solverBuilder, bool EstimateOnlyLinearResponse, int database_size)
        {
            this.rveBuilder = rveBuilder;
            this.solverBuilder = solverBuilder;
            this.EstimateOnlyLinearResponse = EstimateOnlyLinearResponse;
            this.database_size = database_size;            
        }

        private void InitializeData()
        {
            Tuple<Model_v2, Dictionary<int, Node_v2>, double> modelAndBoundaryNodes = this.rveBuilder.GetModelAndBoundaryNodes();
            this.model = modelAndBoundaryNodes.Item1;
            this.boundaryNodes = modelAndBoundaryNodes.Item2;
            this.boundaryElements = GetSubdomainsBoundaryFiniteElementsDictionaries_v2(model, boundaryNodes);
            this.volume = modelAndBoundaryNodes.Item3;
            DefineAppropriateConstraintsForBoundaryNodes();
            this.model.ConnectDataStructures();            
        }


        private void DefineAppropriateConstraintsForBoundaryNodes()
        {
            foreach (Node_v2 boundaryNode in boundaryNodes.Values)
            {
                scaleTransitions.ImposeAppropriateConstraintsPerBoundaryNode(model, boundaryNode);
            }
        }

        private void InitializeFreeAndPrescribedDofsInitialDisplacementVectors()
        {
            uInitialFreeDOFDisplacementsPerSubdomain = new Dictionary<int, IVector>();
            foreach(Subdomain_v2 subdomain in model.SubdomainsDictionary.Values)
            {
                uInitialFreeDOFDisplacementsPerSubdomain.Add(subdomain.ID, Vector.CreateZero(subdomain.DofOrdering.NumFreeDofs));// prosoxh sto Id twn subdomain
            }
            double[] smallStrainVec = new double[6];
            initialConvergedBoundaryDisplacements = new Dictionary<int, Dictionary<DOFType, double>>();
            foreach (Node_v2 boundaryNode in boundaryNodes.Values)
            {
                scaleTransitions.ModifyMicrostructureTotalPrescribedBoundaryDisplacementsVectorForMacroStrainVariable(boundaryNode,
                smallStrainVec, initialConvergedBoundaryDisplacements);
            }            
        }

        public object Clone()
        {
            int new_rve_id = rnd1.Next(1, database_size + 1);
            return new Microstructure3D(rveBuilder.Clone(new_rve_id),solverBuilder.Clone(), EstimateOnlyLinearResponse, database_size);
        }

        public Dictionary<int, Node_v2> BoundaryNodesDictionary
        {
            get { return boundaryNodes; }
        }
        public IList<Node_v2> BoundaryNodes
        {
            get { return boundaryNodes.Values.ToList<Node_v2>(); }
        }

        public void UpdateMaterial(double[] smallStrainVec)
        {
            ISolver_v2 solver;
            if (matrices_not_initialized)
            {
                this.InitializeMatrices();
                this.InitializeData();
                solver = solverBuilder.BuildSolver(model);
                solver.OrderDofsAndClearLinearSystems(); //model.GlobalDofOrdering = solver.DofOrderer.OrderDofs(model); //TODO find out if new structures cause any problems
                solver.ResetSubdomainForcesVector();
                this.InitializeFreeAndPrescribedDofsInitialDisplacementVectors();
            }
            else
            {
                solver = solverBuilder.BuildSolver(model);
                solver.OrderDofsAndClearLinearSystems(); //v2.1
                //solver.ResetSubdomainForcesVector();
                foreach (ILinearSystem_v2 linearSystem in solver.LinearSystems.Values)
                {
                    linearSystem.RhsVector = linearSystem.Subdomain.Forces; //TODO MS 
                }
            }

            for (int i1 = 0; i1 < 6; i1++)
            {
                for (int j1 = 0; j1 < 6; j1++)
                {Cijrs_prev[i1, j1] = constitutiveMatrix[i1, j1];}
            }

            #region Rve prescribed Dofs total DIsplacement Dictionary Creation (nessesary for NRNLAnalyzer)
            // epivolh metakinhsewn ston analyzer pou molis dhmiourghsame --> tha ginetai sto dictionary
            Dictionary<int, Dictionary<DOFType, double>> totalPrescribedBoundaryDisplacements = new Dictionary<int, Dictionary<DOFType, double>>();
            foreach (Node_v2 boundaryNode in boundaryNodes.Values)
            {
                scaleTransitions.ModifyMicrostructureTotalPrescribedBoundaryDisplacementsVectorForMacroStrainVariable(boundaryNode,
                smallStrainVec, totalPrescribedBoundaryDisplacements);
            }
            #endregion
                     
            //var linearSystems = CreateNecessaryLinearSystems(model);    // OPOU pairnei rhs apo subdomainForces       
            //var solver = GetAppropriateSolver(linearSystems);

            
            #region Creation of nessesary analyzers for NRNLAnalyzer and Creation of Microstructure analyzer (NRNLdevelop temporarilly) and solution ;
            int increments = 1; int MaxIterations = 100; int IterationsForMatrixRebuild = 1;
            (MicrostructureBvpNRNLAnalyzer microAnalyzer, ProblemStructural_v2 provider, ElementStructuralStiffnessProvider_v2 elementProvider) = 
                AnalyzeMicrostructure_v2(model, solver, increments, MaxIterations, IterationsForMatrixRebuild,
                totalPrescribedBoundaryDisplacements, initialConvergedBoundaryDisplacements, boundaryNodes, uInitialFreeDOFDisplacementsPerSubdomain);
            #endregion

            #region update of free converged displacements vectors
            uInitialFreeDOFDisplacementsPerSubdomain = microAnalyzer.GetConvergedSolutionVectorsOfFreeDofs();// ousiastika to u pou twra taftizetai me to uPlusuu
            #endregion


            #region INTEGRATION stresses 
            Dictionary<int, IVector> du = microAnalyzer.GetConvergedIncrementalSolutionVectorsOfFreeDofs();
            Dictionary<int, double[]> FppReactionVectorSubdomains = SubdomainCalculationsMultiple_v2.CalculateFppReactionsVectorSubdomains_v2(model, elementProvider, scaleTransitions, boundaryNodes,
                uInitialFreeDOFDisplacementsPerSubdomain, du, initialConvergedBoundaryDisplacements, totalPrescribedBoundaryDisplacements, increments, increments);
            double[] FppReactionVector= SubdomainCalculationsMultiple_v2.CombineMultipleSubdomainsStressesIntegrationVectorsIntoTotal_v2(FppReactionVectorSubdomains);



            double[] DqFpp = SubdomainCalculations_v2.CalculateDqFpp_v2(FppReactionVector, scaleTransitions, boundaryNodes);

            trueStressVec = new double [DqFpp.Length];
            for (int i1 = 0; i1 < DqFpp.Length; i1++)
            { trueStressVec[i1]=(1 / volume) * DqFpp[i1]; }

           
            #endregion

            #region INTEGRATION constitutive Matrix
            var integrationSimultaneous = new SubdomainCalculationsAndAssembly();
            (Dictionary<int, double[][]> KfpDqSubdomains, Dictionary<int, double[][]> KppDqVectorsSubdomains) = 
                integrationSimultaneous.UpdateSubdomainKffAndCalculateKfpDqAndKppDqpMultipleObje_v2(model, elementProvider, scaleTransitions, boundaryNodes, boundaryElements, solver);


            Dictionary<int, double[][]> f2_vectorsSubdomains = SubdomainCalculationsMultiple_v2.CalculateKffinverseKfpDqSubdomains_v2(KfpDqSubdomains, model, elementProvider, scaleTransitions, boundaryNodes, solver);

            Dictionary<int, double[][]> f3_vectorsSubdomains = SubdomainCalculationsMultiple_v2.CalculateKpfKffinverseKfpDqSubdomains_v2(f2_vectorsSubdomains, model, elementProvider, scaleTransitions, boundaryNodes);

            double[][] f3_vectors = SubdomainCalculationsMultiple_v2.CombineMultipleSubdomainsIntegrationVectorsIntoTotal_v2(f3_vectorsSubdomains,scaleTransitions);
            double[][] KppDqVectors = SubdomainCalculationsMultiple_v2.CombineMultipleSubdomainsIntegrationVectorsIntoTotal_v2(KppDqVectorsSubdomains,scaleTransitions);

            double[][] f4_vectors = SubdomainCalculations_v2.SubtractConsecutiveVectors_v2(KppDqVectors, f3_vectors);
            double[,] DqCondDq = SubdomainCalculations_v2.CalculateDqCondDq_v2(f4_vectors, scaleTransitions, boundaryNodes);

            double[,] constitutiveMat = new double[DqCondDq.GetLength(0), DqCondDq.GetLength(1)];
            for (int i1 = 0; i1 < DqCondDq.GetLength(0); i1++)
            {
                for (int i2 = 0; i2 < DqCondDq.GetLength(1); i2++)
                {
                    constitutiveMat[i1, i2] = (1 / volume) * DqCondDq[i1, i2];
                }
            }
            #endregion

            #region update of prescribed converged displacements vectors;
            initialConvergedBoundaryDisplacements = totalPrescribedBoundaryDisplacements;
            #endregion

            #region constitutive tensors transformation methods
            
            
            #endregion

            this.constitutiveMatrix = Matrix.CreateFromArray(constitutiveMat);

            //PrintMethodsForDebug(KfpDq, f2_vectors, f3_vectors, KppDqVectors, f4_vectors, DqCondDq, d2W_dfdf, Cijrs);
            this.modified = CheckIfConstitutiveMatrixChanged(); 
        }        

        private bool CheckIfConstitutiveMatrixChanged()
        {
            for (int i = 0; i < 6; i++)
                for (int j = 0; j < 6; j++)
                    if (Math.Abs(Cijrs_prev[i, j] - constitutiveMatrix[i, j]) > 1e-10)
                        return true;

            return false;
        }



        #region IFiniteElementMaterial3D methodoi mia mia 

        public IMatrixView ConstitutiveMatrix
        {
            get
            {
                if (constitutiveMatrix == null) CalculateOriginalConstitutiveMatrixWithoutNLAnalysis(); // TODOGerasimos arxiko constitutive mporei na upologizetai pio efkola
                return constitutiveMatrix;
            }
        }

        public double[] Stresses // opws xrhsimopoeitai sto mohrcoulomb kai hexa8
        {
            get { return trueStressVec; }
        }

        public void SaveState()
        {
            var subdomainUpdaters = new Dictionary<int, NonLinearSubdomainUpdaterWithInitialConditions_v2>(1); 
            foreach (Subdomain_v2 subdomain in model.Subdomains)//TODO : or else "in model.SubdomainsDictionary.Values)"
            {
                subdomainUpdaters.Add(subdomain.ID, new NonLinearSubdomainUpdaterWithInitialConditions_v2(subdomain)); //v2.3
                //subdomainUpdaters[counter] = new NonLinearSubdomainUpdaterWithInitialConditions(subdomain);
            }
            foreach (var subdomainUpdater in subdomainUpdaters.Values)
            {
                subdomainUpdater.UpdateState();
            }
            
        }

        public bool Modified
        {
            get { return modified; }
        }

        public void ResetModified()
        {
            modified = false;
        }

        public int ID
        {
            get { return 1000; }
        }


        #endregion
        // methodoi ews edw xrhsimopoiountai
        public void ClearState() 
        {
            // pithanws TODO 
        }
        public void ClearStresses()
        {
            // pithanws TODO 
        }
        public double[] Coordinates { get; set; }

        public double YoungModulus => throw new NotSupportedException();

        public double PoissonRatio => throw new NotSupportedException();


        

        public void CalculateOriginalConstitutiveMatrixWithoutNLAnalysis()
        {
            ISolver_v2 solver;
            if (matrices_not_initialized)
            {
                this.InitializeMatrices();
                this.InitializeData();
                solver = solverBuilder.BuildSolver(model);
                solver.OrderDofsAndClearLinearSystems(); //model.GlobalDofOrdering = solver.DofOrderer.OrderDofs(model); //TODO find out if new structures cause any problems
                solver.ResetSubdomainForcesVector();
                this.InitializeFreeAndPrescribedDofsInitialDisplacementVectors();
            }
            else
            {
                solver = solverBuilder.BuildSolver(model);
                solver.OrderDofsAndClearLinearSystems(); //v2.1
                //solver.ResetSubdomainForcesVector();
            }


            var elementProvider = new ElementStructuralStiffnessProvider_v2();                      
            #region INTEGRATION constitutive Matrix            
            var integrationSimultaneous = new SubdomainCalculationsAndAssembly();
            (Dictionary<int, double[][]> KfpDqSubdomains, Dictionary<int, double[][]> KppDqVectorsSubdomains) =
                integrationSimultaneous.UpdateSubdomainKffAndCalculateKfpDqAndKppDqpMultipleObje_v2(model, elementProvider, scaleTransitions, boundaryNodes, boundaryElements, solver);
            Dictionary<int, double[][]> f2_vectorsSubdomains = SubdomainCalculationsMultiple_v2.CalculateKffinverseKfpDqSubdomains_v2(KfpDqSubdomains, model, elementProvider, scaleTransitions, boundaryNodes, solver);

            Dictionary<int, double[][]> f3_vectorsSubdomains = SubdomainCalculationsMultiple_v2.CalculateKpfKffinverseKfpDqSubdomains_v2(f2_vectorsSubdomains, model, elementProvider, scaleTransitions, boundaryNodes);

            double[][] f3_vectors = SubdomainCalculationsMultiple_v2.CombineMultipleSubdomainsIntegrationVectorsIntoTotal_v2(f3_vectorsSubdomains, scaleTransitions);
            double[][] KppDqVectors = SubdomainCalculationsMultiple_v2.CombineMultipleSubdomainsIntegrationVectorsIntoTotal_v2(KppDqVectorsSubdomains, scaleTransitions);

            double[][] f4_vectors = SubdomainCalculations_v2.SubtractConsecutiveVectors_v2(KppDqVectors, f3_vectors);
            double[,] DqCondDq = SubdomainCalculations_v2.CalculateDqCondDq_v2(f4_vectors, scaleTransitions, boundaryNodes);

            double[,] constitutiveMat = new double[DqCondDq.GetLength(0), DqCondDq.GetLength(1)];
            for (int i1 = 0; i1 < DqCondDq.GetLength(0); i1++)
            {
                for (int i2 = 0; i2 < DqCondDq.GetLength(1); i2++)
                {
                    constitutiveMat[i1, i2] = (1 / volume) * DqCondDq[i1, i2];
                }
            }
            
            #endregion

            #region constitutive tensors transformation methods                       
            constitutiveMatrix = Matrix.CreateFromArray(constitutiveMat);
            #endregion

            //PrintMethodsForDebug(KfpDq, f2_vectors, f3_vectors, KppDqVectors, f4_vectors, DqCondDq, d2W_dfdf, Cijrs);
            this.modified = CheckIfConstitutiveMatrixChanged();

            if (EstimateOnlyLinearResponse)
            {
                model = null;
                boundaryElements = null;
                boundaryNodes = null;
                rveBuilder = null;
                uInitialFreeDOFDisplacementsPerSubdomain = null;
                initialConvergedBoundaryDisplacements = null;
                Cijrs_prev = null;
            }
        }

        #region transformation methods
        //TODO: these methods can be deleted or implemented only for the case of an oriented rve
        private double[] transformTrueStressVec(double[] trueStressVec, double[] tangent1, double[] tangent2, double[] normal)
        {
            throw new NotImplementedException();
        }

        private double[,] TransformConstitutiveMatrix(double[,] constitutiveMat, double[] tangent1, double[] tangent2, double[] normal)
        {
            throw new NotImplementedException();
        }

        #endregion

        #region Print methods for debug
        //private void PrintMethodsForDebug(double[][] KfpDq, double[][] f2_vectors, double[][] f3_vectors, double[][] KppDqVectors, double[][] f4_vectors, double[,] DqCondDq, double[,] d2W_dfdf, double[,] Cijrs)
        //{
        //    string string0 = @"C:\Users\turbo-x\Desktop\notes_elegxoi\REFERENCE_kanonikh_gewmetria_2\REF2_10__000_renu_new_multiple_algorithms_check_develop_1GrSh_correct_coh_CHECK_integration\d2\";

        //    string string1 = String.Concat(string0, @"KfpDq_{0}.txt");

        //    for (int i2 = 0; i2 < KfpDq.GetLength(0); i2++)
        //    {
        //        string path = string.Format(string1, (i2 + 1).ToString());
        //        Vector data = new Vector(KfpDq[i2]);
        //        data.WriteToFile(path);
        //    }

        //    string string2 = String.Concat(string0, @"KffInvKfpDq_{0}.txt");



        //    for (int i2 = 0; i2 < f2_vectors.GetLength(0); i2++)
        //    {
        //        string path = string.Format(string2, (i2 + 1).ToString());
        //        Vector data = new Vector(f2_vectors[i2]);
        //        data.WriteToFile(path);
        //    }

        //    string string3 = String.Concat(string0, @"f3_vectors_{0}.txt");
        //    string string4 = String.Concat(string0, @"KppDqVectors_{0}.txt");
        //    string string5 = String.Concat(string0, @"f4_vectors_{0}.txt");

        //    for (int i2 = 0; i2 < f2_vectors.GetLength(0); i2++)
        //    {
        //        string path = string.Format(string3, (i2 + 1).ToString());
        //        Vector data = new Vector(f3_vectors[i2]);
        //        data.WriteToFile(path);

        //        path = string.Format(string4, (i2 + 1).ToString());
        //        data = new Vector(KppDqVectors[i2]);
        //        data.WriteToFile(path);

        //        path = string.Format(string5, (i2 + 1).ToString());
        //        data = new Vector(f4_vectors[i2]);
        //        data.WriteToFile(path);

        //    }

        //    PrintUtilities.WriteToFile(DqCondDq, String.Concat(string0, @"DqCondDq.txt"));
        //    PrintUtilities.WriteToFile(d2W_dfdf,  String.Concat(string0, @"d2W_dfdf.txt"));
        //    PrintUtilities.WriteToFile(Cijrs, String.Concat(string0, @"Cijrs.txt"));
        //}
        #endregion


    }
    //Microstructure3DevelopMultipleSubdomainsUseBaseSimuRandObj_v2SmallStrains3D
    //Origin  Microstructure3DevelopMultipleSubdomainsUseBaseSimuRandObj_v2
    //modifications apo defgrad egine smallstrains2d me odhgo Microstructure3DevelopMultipleSubdomainsUseBaseSmallStrains2D se sxesh me to Microstructure3DevelopMultipleSubdomainsUseBase.cs




}