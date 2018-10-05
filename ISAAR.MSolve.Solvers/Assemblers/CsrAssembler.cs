﻿using System.Collections.Generic;
using ISAAR.MSolve.Discretization.Interfaces;
using ISAAR.MSolve.FEM.Elements;
using ISAAR.MSolve.LinearAlgebra.Matrices;
using ISAAR.MSolve.LinearAlgebra.Matrices.Builders;
using ISAAR.MSolve.Solvers.Commons;
using ISAAR.MSolve.Solvers.Ordering;
using LegacyVector = ISAAR.MSolve.Numerical.LinearAlgebra.Vector;

//TODO: this should work with MatrixProviders instead of asking the elements directly.
namespace ISAAR.MSolve.Solvers.Assemblers
{
    /// <summary>
    /// Builds the global matrix of the linear system that will be solved. This matrix is in CSR format.
    /// Authors: Serafeim Bakalakos
    /// </summary>
    public class CsrAssembler: IGlobalMatrixAssembler
    {
        private const string name = "CsrAssembler"; // for error messages
        private readonly LinearSystem_v2<CsrMatrix, LegacyVector> linearSystem;
        private readonly bool sortColsOfEachRow;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sortColsOfEachRow">Sorting the columns of each row in the CSR storage format may increase performance 
        ///     of the matrix vector multiplications. It is recommended to set it to true, especially for iterative linear 
        ///     system solvers.</param>
        public CsrAssembler(IReadOnlyList<LinearSystem_v2<CsrMatrix, LegacyVector>> linearSystems, 
            bool sortColsOfEachRow = true)
        {
            if (linearSystems.Count != 1) throw new InvalidMatrixFormatException(
                name + " can be used if there is only 1 subdomain.");
            this.linearSystem = linearSystems[0];
            this.sortColsOfEachRow = sortColsOfEachRow;
        }

        public (CsrMatrix Kff, DokRowMajor Kfc) BuildGlobalMatrices(IEnumerable<IElement> elements,
            AllDofOrderer dofOrderer, IElementMatrixProvider matrixProvider)
        {
            int numConstrainedDofs = dofOrderer.NumConstrainedDofs;
            int numFreeDofs = dofOrderer.NumFreeDofs;
            var Kff = DokRowMajor.CreateEmpty(numFreeDofs, numFreeDofs);
            var Kfc = DokRowMajor.CreateEmpty(numFreeDofs, numConstrainedDofs);

            foreach (IElement elementWrapper in elements)
            {
                ContinuumElement2D element = (ContinuumElement2D)(elementWrapper.IElementType);
                // TODO: perhaps that could be done and cached during the dof enumeration to avoid iterating over the dofs twice
                (IReadOnlyDictionary<int, int> mapStandard, IReadOnlyDictionary<int, int> mapConstrained) =
                    dofOrderer.MapDofsElementToGlobal(element);
                Matrix k = Conversions.MatrixOldToNew(matrixProvider.Matrix(elementWrapper));
                Kff.AddSubmatrixSymmetric(k, mapStandard);
                Kfc.AddSubmatrix(k, mapStandard, mapConstrained);
            }

            //TODO: perhaps I should filter the matrices in the concrete class before returning (e.g. dok.Build())
            return (Kff.BuildCsrMatrix(sortColsOfEachRow), Kfc);
        }

        public void BuildGlobalMatrix(IEnumerable<IElement> elements, FreeDofOrderer dofOrderer, 
            IElementMatrixProvider matrixProvider)
        {
            int numFreeDofs = dofOrderer.NumFreeDofs;
            var Kff = DokRowMajor.CreateEmpty(numFreeDofs, numFreeDofs);

            foreach (IElement elementWrapper in elements)
            {
                ContinuumElement2D element = (ContinuumElement2D)(elementWrapper.IElementType);
                // TODO: perhaps that could be done and cached during the dof enumeration to avoid iterating over the dofs twice
                IReadOnlyDictionary<int, int> mapStandard = dofOrderer.MapFreeDofsElementToGlobal(element);
                Matrix k = Conversions.MatrixOldToNew(matrixProvider.Matrix(elementWrapper));
                Kff.AddSubmatrixSymmetric(k, mapStandard);
            }

            linearSystem.Matrix = Kff.BuildCsrMatrix(sortColsOfEachRow);
            linearSystem.IsMatrixModified = true;
        }
    }
}
