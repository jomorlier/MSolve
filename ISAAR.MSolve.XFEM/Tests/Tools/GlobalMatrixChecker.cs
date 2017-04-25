﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ISAAR.MSolve.Numerical.LinearAlgebra;
using ISAAR.MSolve.Numerical.LinearAlgebra.Interfaces;
using ISAAR.MSolve.XFEM.Assemblers;
using ISAAR.MSolve.XFEM.Elements;
using ISAAR.MSolve.XFEM.Entities;
using ISAAR.MSolve.XFEM.Entities.FreedomDegrees;
using ISAAR.MSolve.XFEM.LinearAlgebra;

namespace ISAAR.MSolve.XFEM.Tests.Tools
{
    class GlobalMatrixChecker
    {
        private readonly string expectedMatrixPath;
        private readonly string expectedDofEnumerationPath;
        private readonly bool printIncorrectEntries;
        private readonly ValueComparer comparer;

        public GlobalMatrixChecker(string expectedMatrixPath, string expectedDofEnumerationPath, 
            double tolerance = 1e-4, bool printIncorrectEntries = true)
        {
            this.expectedMatrixPath = expectedMatrixPath;
            this.expectedDofEnumerationPath = expectedDofEnumerationPath;
            this.printIncorrectEntries = printIncorrectEntries;
            this.comparer = new ValueComparer(tolerance);
        }

        public void PrintGlobalMatrix(Model2D model, bool nodeMajorReordering = false)
        {
            Console.WriteLine("Global stiffness matrix:");
            SkylineMatrix2D Kff;
            Matrix2D Kfc;
            SingleGlobalSkylineAssembler.BuildGlobalMatrix(model, out Kff, out Kfc);
            int[] permutation = DofReorder.OldToNewDofs(model, OutputReaders.ReadNodalDofs(expectedDofEnumerationPath));
            MatrixUtilities.PrintDense(MatrixUtilities.Reorder(Kff, permutation));
        }

        public void CheckGlobalMatrix(Model2D model)
        {
            Console.WriteLine("Checking global stiffness matrix...");
            var errors = new StringBuilder("Errors at entries: ");
            bool isCorrect = true;

            // Retrieve the matrices
            Matrix2D expectedMatrix = OutputReaders.ReadGlobalStiffnessMatrix(expectedMatrixPath);
            SkylineMatrix2D Kff;
            Matrix2D Kfc;
            SingleGlobalSkylineAssembler.BuildGlobalMatrix(model, out Kff, out Kfc);
            int[] permutation = DofReorder.OldToNewDofs(model, OutputReaders.ReadNodalDofs(expectedDofEnumerationPath));
            IMatrix2D actualMatrix = MatrixUtilities.Reorder(Kff, permutation);

            // Check dimensions first
            if (actualMatrix.Rows != expectedMatrix.Rows)
                throw new ArgumentException("The 2 global matrices have non matching rows.");
            if (actualMatrix.Columns != expectedMatrix.Columns)
                throw new ArgumentException("The 2 global matrices have non matching columns.");

            // Check each entry
            for (int row = 0; row < actualMatrix.Rows; ++row)
            {
                for (int col = 0; col < actualMatrix.Columns; ++col)
                {
                    if (!comparer.AreEqual(actualMatrix[row, col], expectedMatrix[row, col]))
                    {
                        errors.Append("[").Append(row).Append(", ").Append(col).Append("] ");
                        isCorrect = false;
                    }
                }
            }
            if (isCorrect) Console.WriteLine("Global stiffness matrix is correct!\n");
            else if (printIncorrectEntries) Console.WriteLine(errors.Append("\n").ToString());
            else Console.WriteLine("Wrong global stiffness matrix!\n");

        }
    }
}
