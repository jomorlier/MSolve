﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ISAAR.MSolve.XFEM.Elements;
using ISAAR.MSolve.XFEM.Enrichments.Items;
using ISAAR.MSolve.XFEM.Entities;
using ISAAR.MSolve.XFEM.Geometry.CoordinateSystems;
using ISAAR.MSolve.XFEM.Geometry.Shapes;
using ISAAR.MSolve.XFEM.Geometry.Triangulation;
using ISAAR.MSolve.XFEM.Interpolation;
using ISAAR.MSolve.XFEM.Geometry.Mesh;

namespace ISAAR.MSolve.XFEM.CrackGeometry
{
    class BasicExplicitCrack2D: ICrackDescription
    {
        private static readonly PointComparer pointComparer = new PointComparer();

        private readonly double tipEnrichmentAreaRadius;
        private readonly CartesianTriangulator triangulator;

        // TODO: Not too fond of the setters, but at least the enrichments are immutable. Perhaps I can pass their
        // parameters to a CrackDescription builder and construct them there, without involving the user 
        // (given how easy it is to forget the setters, it is a must).
        public IMesh2D<XNode2D, XContinuumElement2D> Mesh { get; set; }
        public CrackBodyEnrichment2D CrackBodyEnrichment { get; set; }
        public CrackTipEnrichments2D CrackTipEnrichments { get; set; }
        public ICartesianPoint2D CrackTip { get { return Vertices[Vertices.Count - 1]; } }
        public List<XContinuumElement2D> TipElements { get; }

        private List<ICartesianPoint2D> Vertices { get; }
        private List<DirectedSegment2D> Segments { get; }
        private List<double> Angles { get; } // Angle of segment i w.r.t segment i-1, aka the crack growth angle

        public BasicExplicitCrack2D(double tipEnrichmentAreaRadius = 0.0)
        {
            this.tipEnrichmentAreaRadius = tipEnrichmentAreaRadius;
            this.triangulator = new CartesianTriangulator();
            this.TipElements = new List<XContinuumElement2D>();

            Vertices = new List<ICartesianPoint2D>();
            Segments = new List<DirectedSegment2D>();
            Angles = new List<double>();
        }

        public TipCoordinateSystem TipSystem { get; private set; }

        public void InitializeGeometry(ICartesianPoint2D crackMouth, ICartesianPoint2D crackTip)
        {
            double dx = crackTip.X - crackMouth.X;
            double dy = crackTip.Y - crackMouth.Y;
            double tangentSlope = Math.Atan2(dy, dx);
            TipSystem = new TipCoordinateSystem(crackTip, tangentSlope);

            Vertices.Add(crackMouth);
            Vertices.Add(crackTip);
            Segments.Add(new DirectedSegment2D(crackMouth, crackTip));
            Angles.Add(double.NaN);
        }

        public void UpdateGeometry(double localGrowthAngle, double growthLength)
        {
            double globalGrowthAngle = localGrowthAngle + TipSystem.RotationAngle;
            double dx = growthLength * Math.Cos(globalGrowthAngle);
            double dy = growthLength * Math.Sin(globalGrowthAngle);
            double tangentSlope = Math.Atan2(dy, dx);

            var oldTip = Vertices[Vertices.Count - 1];
            var newTip = new CartesianPoint2D(oldTip.X + dx, oldTip.Y + dy);
            Vertices.Add(newTip);
            Segments.Add(new DirectedSegment2D(oldTip, newTip));
            Angles.Add(localGrowthAngle); // These are independent of the global coordinate system
            TipSystem = new TipCoordinateSystem(newTip, tangentSlope);
        }

        public double SignedDistanceOf(XNode2D node)
        {
            return SignedDistanceOfPoint(node);
        }

        public double SignedDistanceOf(INaturalPoint2D point, IReadOnlyList<XNode2D> elementNodes,
             EvaluatedInterpolation2D interpolation)
        {
            return SignedDistanceOfPoint(interpolation.TransformPointNaturalToGlobalCartesian(point));
        }

        public Tuple<double, double> SignedDistanceGradientThrough(INaturalPoint2D point,
             IReadOnlyList<XNode2D> elementNodes, EvaluatedInterpolation2D interpolation)
        {
            throw new NotImplementedException("Perhaps the private method should return the closest segment");
        }

        public IReadOnlyList<TriangleCartesian2D> TriangulateAreaOf(XContinuumElement2D element)
        {
            var polygon = ConvexPolygon2D.CreateUnsafe(element.Nodes);
            var triangleVertices = new SortedSet<ICartesianPoint2D>(element.Nodes, pointComparer);
            int nodesCount = element.Nodes.Count;

            foreach (var vertex in Vertices)
            {
                PolygonPointPosition position = polygon.FindRelativePositionOfPoint(vertex);
                if (position == PolygonPointPosition.Inside || position == PolygonPointPosition.OnEdge || 
                    position == PolygonPointPosition.OnVertex) triangleVertices.Add(vertex);
            }

            foreach (var crackSegment in Segments)
            {
                var segment = new LineSegment2D(crackSegment.Start, crackSegment.End);
                IReadOnlyList<ICartesianPoint2D> intersections = segment.IntersectionWith(polygon);
                foreach (var point in intersections)
                {
                    triangleVertices.Add(point);
                }
            }

            return triangulator.CreateMesh(triangleVertices);
        }

        public void UpdateEnrichments()
        {
            var bodyNodes = new HashSet<XNode2D>();
            var tipNodes = new HashSet<XNode2D>();
            TipElements.Clear();

            FindBodyAndTipNodesAndElements(bodyNodes, tipNodes, TipElements);
            ApplyFixedEnrichmentArea(tipNodes, TipElements[0]);
            ResolveHeavisideEnrichmentDependencies(bodyNodes);

            ApplyEnrichmentFunctions(bodyNodes, tipNodes);
        }

        private void ApplyEnrichmentFunctions(HashSet<XNode2D> bodyNodes, HashSet<XNode2D> tipNodes)
        {
            // O(n) operation. TODO: This could be sped up by tracking the tip enriched nodes of each step.
            foreach (var node in Mesh.Vertices) node.EnrichmentItems.Remove(CrackTipEnrichments);
            foreach (var node in tipNodes)
            {
                double[] enrichmentValues = CrackTipEnrichments.EvaluateFunctionsAt(node);
                node.EnrichmentItems[CrackTipEnrichments] = enrichmentValues;
            }

            // Heaviside enrichment is never removed (unless the crack curves towards itself, but that creates a lot of
            // problems and cannot be modeled with LSM accurately). Thus there is no need to process each mesh node. 
            // TODO: It could be sped up by only updating the Heaviside enrichments of nodes that have updated body  
            // level sets, which requires tracking them.
            foreach (var node in bodyNodes)
            {
                double[] enrichmentValues = CrackBodyEnrichment.EvaluateFunctionsAt(node);
                node.EnrichmentItems[CrackBodyEnrichment] = enrichmentValues;
            }
        }

        /// <summary>
        /// If a fixed enrichment area is applied, all nodes inside a circle around the tip are enriched with tip 
        /// functions. They can still be enriched with Heaviside functions, if they do not belong to the tip 
        /// element(s).
        /// </summary>
        /// <param name="tipNodes"></param>
        /// <param name="tipElement"></param>
        private void ApplyFixedEnrichmentArea(HashSet<XNode2D> tipNodes, XContinuumElement2D tipElement)
        {
            if (tipEnrichmentAreaRadius > 0)
            {
                var enrichmentArea = new Circle2D(Vertices[Vertices.Count - 1], tipEnrichmentAreaRadius);
                foreach (var element in Mesh.FindElementsInsideCircle(enrichmentArea, tipElement))
                {
                    bool completelyInside = true;
                    foreach (var node in element.Nodes)
                    {
                        CirclePointPosition position = enrichmentArea.FindRelativePositionOfPoint(node);
                        if ((position == CirclePointPosition.Inside) || (position == CirclePointPosition.On))
                        {
                            tipNodes.Add(node);
                        }
                        else completelyInside = false;
                    }
                    if (completelyInside) element.EnrichmentItems.Add(CrackTipEnrichments);
                }

                #region alternatively
                /* // If there wasn't a need to enrich the elements, this is more performant
                foreach (var node in mesh.FindNodesInsideCircle(enrichmentArea, true, tipElement))
                {
                    tipNodes.Add(node); // Nodes of tip element(s) will not be included twice
                } */
                #endregion
            }
        }

        private ElementEnrichmentType CharacterizeElementEnrichment(XContinuumElement2D element)
        {
            var polygon = ConvexPolygon2D.CreateUnsafe(element.Nodes);
            int tipIndex = Vertices.Count - 1;

            // Check tip element
            PolygonPointPosition tipPosition = polygon.FindRelativePositionOfPoint(Vertices[tipIndex]);
            if (tipPosition == PolygonPointPosition.Inside || tipPosition == PolygonPointPosition.OnEdge ||
                    tipPosition == PolygonPointPosition.OnVertex)
            {
                PolygonPointPosition previousVertexPos = polygon.FindRelativePositionOfPoint(Vertices[tipIndex - 1]);
                if (previousVertexPos == PolygonPointPosition.Inside)
                {
                    throw new NotImplementedException("Problem with blending elements, if the tip element is also " +
                        "enriched with Heaviside. What happens after the crack tip? Based on the LSM, the signed " +
                        "distance of the blending element after the crack tip should have a positive and negative " +
                        "region, however that element is not split by the crack and  thus should not have " +
                        "discontinuity in the displacement field");
                    //return ElementEnrichmentType.Both;
                }
                else return ElementEnrichmentType.Tip;
            }

            // Look at the other vertices 
            // (if a segment is inside an element, it will not be caught by checking the segment itself)
            bool previousVertexOnEdge = false;
            for (int v = 0; v < tipIndex; ++v)
            {
                PolygonPointPosition position = polygon.FindRelativePositionOfPoint(Vertices[v]);
                if (position == PolygonPointPosition.Inside) return ElementEnrichmentType.Heaviside;
                else if (position == PolygonPointPosition.OnEdge || position == PolygonPointPosition.OnVertex)
                {
                    if (previousVertexOnEdge) return ElementEnrichmentType.Heaviside;
                    else previousVertexOnEdge = true;
                }
                else previousVertexOnEdge = false;
            }

            // Look at each segment
            foreach (var crackSegment in Segments)
            {
                var segment = new LineSegment2D(crackSegment.Start, crackSegment.End);
                CartesianPoint2D intersectionPoint;
                foreach (var edge in polygon.Edges)
                {
                    LineSegment2D.SegmentSegmentPosition position = segment.IntersectionWith(edge, out intersectionPoint);
                    if (position == LineSegment2D.SegmentSegmentPosition.Intersecting)
                    {
                        // TODO: Perhaps the element should not be flagged as a Heaviside element, if the segment passes
                        // through 1 node only. To detect this, check if the intersection point coincides with an element
                        // node. If it does store it and go to the next edge. If a second intersection point (that does
                        // not coincide with the stored one) is found then it is a Heaviside element.
                        return ElementEnrichmentType.Heaviside;
                    }
                    else if (position == LineSegment2D.SegmentSegmentPosition.Overlapping)
                    {
                        return ElementEnrichmentType.Heaviside;
                    }
                }
            }

            // Then it must be a standard element
            return ElementEnrichmentType.Standard;
        }

        private void FindBodyAndTipNodesAndElements(HashSet<XNode2D> bodyNodes, HashSet<XNode2D> tipNodes,
            List<XContinuumElement2D> tipElements)
        {
            var bothElements = new HashSet<XContinuumElement2D>();
            foreach (var element in Mesh.Faces)
            {
                element.EnrichmentItems.Clear();
                ElementEnrichmentType type = CharacterizeElementEnrichment(element);
                if (type == ElementEnrichmentType.Tip)
                {
                    tipElements.Add(element);
                    foreach (var node in element.Nodes) tipNodes.Add(node);
                    element.EnrichmentItems.Add(CrackTipEnrichments);
                }
                else if (type == ElementEnrichmentType.Heaviside)
                {
                    foreach (var node in element.Nodes) bodyNodes.Add(node);
                    element.EnrichmentItems.Add(CrackBodyEnrichment);
                }
                else if (type == ElementEnrichmentType.Both)
                {
                    tipElements.Add(element);
                    bothElements.Add(element);
                    foreach (var node in element.Nodes)
                    {
                        tipNodes.Add(node);
                        bodyNodes.Add(node);
                    }
                    element.EnrichmentItems.Add(CrackTipEnrichments);
                    element.EnrichmentItems.Add(CrackBodyEnrichment);
                }
            }

            // After all Heaviside nodes are aggregated remove the nodes of tip elements
            foreach (var element in tipElements)
            {
                foreach (var node in element.Nodes) bodyNodes.Remove(node);
            }
            foreach (var element in bothElements) // Re-adding these nodes afterwards is safer 
            {
                foreach (var node in element.Nodes) bodyNodes.Add(node);
            }

            ReportTipElements(tipElements);
        }

        private void FindSignedAreasOfElement(XContinuumElement2D element,
            out double positiveArea, out double negativeArea)
        {
            positiveArea = 0.0;
            negativeArea = 0.0;
            foreach (var triangle in TriangulateAreaOf(element))
            {
                ICartesianPoint2D v0 = triangle.Vertices[0];
                ICartesianPoint2D v1 = triangle.Vertices[1];
                ICartesianPoint2D v2 = triangle.Vertices[2];
                double area = 0.5 * Math.Abs(v0.X * (v1.Y - v2.Y) + v1.X * (v2.Y - v0.Y) + v2.X * (v0.Y - v1.Y));

                // The sign of the area can be derived from any node with signed distance != 0
                int sign = 0;
                foreach (var vertex in triangle.Vertices)
                {
                    sign = Math.Sign(SignedDistanceOfPoint(vertex));
                    if (sign != 0) break;
                }

                // If no node with non-zero signed distance is found, then find the signed distance of its centroid
                if (sign == 0)
                {
                    // Report this instance in DEBUG messages. It should not happen.
                    Console.WriteLine("--- DEBUG: Triangulation resulted in a triangle where all vertices are on the crack. ---");
                    var centroid = new CartesianPoint2D((v0.X + v1.X + v2.X) / 3.0, (v0.Y + v1.Y + v2.Y) / 3.0);
                    sign = Math.Sign(SignedDistanceOfPoint(centroid));
                }

                if (sign > 0) positiveArea += area;
                else if (sign < 0) negativeArea += area;
                else throw new Exception(
                    "Even after finding the signed distance of its centroid, the sign of the area is unidentified");
            }
        }

        private int IndexOfMinAbs(IReadOnlyList<double> distances)
        {
            double min = double.MaxValue;
            int pos = -1;
            for (int i = 0; i < distances.Count; ++i)
            {
                double absDistance = Math.Abs(distances[i]);
                if (absDistance < min)
                {
                    min = absDistance;
                    pos = i;
                }
            }
            return pos;
        }

        private void ResolveHeavisideEnrichmentDependencies(HashSet<XNode2D> bodyNodes)
        {
            const double toleranceHeavisideEnrichmentArea = 1e-4;
            var processedElements = new Dictionary<XContinuumElement2D, Tuple<double, double>>();
            foreach (var node in bodyNodes)
            {
                double nodePositiveArea = 0.0;
                double nodeNegativeArea = 0.0;

                foreach (var element in Mesh.FindElementsWithNode(node))
                {
                    Tuple<double, double> elementPosNegAreas;
                    bool alreadyProcessed = processedElements.TryGetValue(element, out elementPosNegAreas);
                    if (!alreadyProcessed)
                    {
                        double elementPosArea, elementNegArea;
                        FindSignedAreasOfElement(element, out elementPosArea, out elementNegArea);
                        elementPosNegAreas = new Tuple<double, double>(elementPosArea, elementNegArea);
                        processedElements[element] = elementPosNegAreas;
                    }
                    nodePositiveArea += elementPosNegAreas.Item1;
                    nodeNegativeArea += elementPosNegAreas.Item2;
                }

                if (SignedDistanceOfPoint(node) >= 0.0)
                {
                    double negativeAreaRatio = nodeNegativeArea / (nodePositiveArea + nodeNegativeArea);
                    if (negativeAreaRatio < toleranceHeavisideEnrichmentArea) bodyNodes.Remove(node);
                }
                else
                {
                    double positiveAreaRatio = nodePositiveArea / (nodePositiveArea + nodeNegativeArea);
                    if (positiveAreaRatio < toleranceHeavisideEnrichmentArea) bodyNodes.Remove(node);
                }
            }
        }

        private double SignedDistanceOfPoint(ICartesianPoint2D globalPoint)
        {
            var distances = new List<double>();
            bool afterPreviousSegment = false;

            // First segment
            ICartesianPoint2D localPoint = Segments[0].TransformGlobalToLocalPoint(globalPoint);
            if (localPoint.X < Segments[0].Length) distances.Add(localPoint.Y);
            else afterPreviousSegment = true;

            // Subsequent segments
            for (int i = 1; i < Segments.Count - 1; ++i)
            {
                localPoint = Segments[i].TransformGlobalToLocalPoint(globalPoint);
                if (localPoint.X < 0.0)
                {
                    if (afterPreviousSegment)
                    {
                        // Compute the distance from the vertex between this segment and the previous
                        double dx = globalPoint.X - Vertices[i].X;
                        double dy = globalPoint.Y - Vertices[i].Y;
                        double distance = Math.Sqrt(dx * dx + dy * dy);
                        int sign = -Math.Sign(Angles[i]); // If growth angle > 0, the convex angle faces the positive area.
                        distances.Add(sign * distance);
                    }
                    afterPreviousSegment = false;
                }
                else if (localPoint.X <= Segments[i].Length)
                {
                    distances.Add(localPoint.Y);
                    afterPreviousSegment = false;
                }
                else afterPreviousSegment = true;
            }

            // Last segment
            int last = Segments.Count - 1;
            localPoint = Segments[last].TransformGlobalToLocalPoint(globalPoint);
            if (localPoint.X < 0.0)
            {
                if (afterPreviousSegment)
                {
                    // Compute the distance from the vertex between this segment and the previous
                    double dx = globalPoint.X - Vertices[last].X;
                    double dy = globalPoint.Y - Vertices[last].Y;
                    double distance = Math.Sqrt(dx * dx + dy * dy);
                    int sign = -Math.Sign(Angles[last]); // If growth angle > 0, the convex angle faces the positive area.
                    distances.Add(sign * distance);
                }
                afterPreviousSegment = false;
            }
            else distances.Add(localPoint.Y);

            return distances[IndexOfMinAbs(distances)];

        }

        [ConditionalAttribute("DEBUG")]
        private void ReportTipElements(IReadOnlyList<XContinuumElement2D> tipElements)
        {
            Console.WriteLine("------ DEBUG/ ------");
            if (tipElements.Count < 1) throw new Exception("No tip element found");
            Console.WriteLine("Tip elements:");
            for (int e = 0; e < tipElements.Count; ++e)
            {
                Console.WriteLine("Tip element " + e + " with nodes: ");
                foreach (var node in tipElements[e].Nodes)
                {
                    Console.WriteLine(node);
                }
                Console.WriteLine("------ /DEBUG ------");
            }
        }

        /// <summary>
        /// Represents the type of enrichment that will be applied to all nodes of the element. 
        /// </summary>
        private enum ElementEnrichmentType { Standard, Heaviside, Tip, Both }

        private class PointComparer: IComparer<ICartesianPoint2D>
        {
            public int Compare(ICartesianPoint2D point1, ICartesianPoint2D point2)
            {
                if (point1.X < point2.X) return -1;
                else if (point1.X > point2.X) return 1;
                else // same X
                {
                    if (point1.Y < point2.Y) return -1;
                    else if (point1.Y > point2.Y) return 1;
                    else return 0; // same point
                }
            }
        }
    }
}
