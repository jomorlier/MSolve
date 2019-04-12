﻿using ISAAR.MSolve.Discretization.FreedomDegrees;
using ISAAR.MSolve.FEM.Elements;
using ISAAR.MSolve.FEM.Entities;
using MGroup.Stochastic.Interfaces;
using MGroup.Stochastic.Structural.StochasticRealizers;

namespace MGroup.Stochastic.Structural
{
    public class ModelBuilder
    {
        public Model GetModel(RandomVariable randomVariable, IStochasticDomainMapper domainMapper, int iteration)
        {
            var m = new Model();
            m.NodesDictionary.Add(0, new Node() { ID = 0, X = 0, Y = 0, Z = 0 });
            m.NodesDictionary.Add(1, new Node() { ID = 1, X = 1, Y = 0, Z = 0 });
            m.ElementsDictionary.Add(1, new Element()
            {
                ID = 1,
                ElementType = new EulerBeam3D(randomVariable.Realize(iteration, domainMapper, null), 0.3)
            });
            m.Loads.Add(new Load() { Amount = 10, DOF = StructuralDof.TranslationX, Node = m.NodesDictionary[1] });

            return m;
        }
    }
}