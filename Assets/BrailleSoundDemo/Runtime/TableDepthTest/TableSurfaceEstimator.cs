using System;
using System.Collections.Generic;
using Meta.XR;
using UnityEngine;

namespace BrailleSoundDemo.TableDepthTest
{
    /// <summary>
    /// Estimates a small horizontal surface patch from current environment ray hits.
    /// This is geometric validation, not table recognition or an accuracy guarantee.
    /// World-space input must use the same tracking origin and sample interval.
    /// </summary>
    public static class TableSurfaceEstimator
    {
        public struct Estimate
        {
            public Vector3 Center;
            public Vector3 Normal;
            public float Height;
            /// <summary>Range of accepted heights; repeatability, not absolute accuracy.</summary>
            public float HeightSpread;
            public float HeadDistance;
            public float VerticalGap;
            public int InlierCount;
            public int SampleCount;
        }

        public sealed class Settings
        {
            public int MinimumHits = 5;
            public float MinimumInlierFraction = .75f;
            public float MinimumUpDot = .85f;
            public float MinimumNormalConfidence = .5f;
            public float MaximumHeightDeviation = .025f;
            public float MaximumHeightSpread = .035f;
            public float MinimumNormalAgreement = .90f;
            public float MinimumPatchWidth = .015f;
            public float MaximumPatchDiameter = .8f;
            public float MinimumHeadDistance = .15f;
            public float MaximumHeadDistance = 3f;
            public float MinimumVerticalGap = .05f;
        }

        struct Candidate
        {
            public int Index;
            public Vector3 Point;
            public Vector3 Normal;
        }

        /// <summary>
        /// Checks current ray hits against an already locked horizontal reference.
        /// The caller supplies a distributed patch of rays, including misses. A central
        /// hand occluder may be ignored; this method never changes the reference height.
        /// Counts and height spread describe current support, not measurement accuracy.
        /// </summary>
        public static bool TryValidateReference(IReadOnlyList<EnvironmentRaycastHit> hits,
            float referenceHeight, out int acceptedCount, out float heightSpread, out string reason)
        {
            acceptedCount = 0;
            heightSpread = 0;
            if (!Finite(referenceHeight)) { reason = "Invalid reference height"; return false; }
            if (hits == null || hits.Count < 5) { reason = "Too few reference samples"; return false; }
            float lowest = float.PositiveInfinity, highest = float.NegativeInfinity;
            var matches = new List<Candidate>(hits.Count);
            foreach (var hit in hits)
            {
                if (hit.status != EnvironmentRaycastHitStatus.Hit || !Finite(hit.point) || !Finite(hit.normal) ||
                    !Finite(hit.normalConfidence) || hit.normalConfidence < .5f || hit.normalConfidence > 1 ||
                    hit.normal.sqrMagnitude < .0001f || Vector3.Dot(hit.normal.normalized, Vector3.up) < .85f ||
                    Mathf.Abs(hit.point.y-referenceHeight) > .025f) continue;
                ++acceptedCount;
                matches.Add(new Candidate { Point = hit.point, Normal = hit.normal.normalized });
                lowest = Mathf.Min(lowest,hit.point.y);
                highest = Mathf.Max(highest,hit.point.y);
            }
            if (acceptedCount > 0) heightSpread = highest-lowest;
            int required = Math.Max(5,Mathf.CeilToInt(hits.Count*.55f));
            if (acceptedCount < required)
            { reason = "Not enough visible surface at the locked height"; return false; }
            if (!TryValidateFootprint(matches,.015f,.8f,out reason)) return false;
            reason = "Locked surface has current depth support";
            return true;
        }

        /// <summary>
        /// Include misses in hits so sparse or partly occluded patches do not appear complete.
        /// The central gaze hit must be a member of the accepted height cluster.
        /// </summary>
        public static bool TryEstimate(IReadOnlyList<EnvironmentRaycastHit> hits, int centerIndex,
            Vector3 headPosition, out Estimate estimate, out string reason, Settings settings = null)
        {
            estimate = default;
            settings = settings ?? new Settings();
            if (!ValidSettings(settings)) { reason = "Invalid estimator settings"; return false; }
            if (!Finite(headPosition)) { reason = "Invalid headset position"; return false; }
            if (hits == null || hits.Count < settings.MinimumHits || centerIndex < 0 || centerIndex >= hits.Count)
            { reason = "Too few surface samples"; return false; }

            var candidates = new List<Candidate>(hits.Count);
            var heights = new List<float>(hits.Count);
            bool centerValid = false;
            for (int i = 0; i < hits.Count; ++i)
            {
                var hit = hits[i];
                if (hit.status != EnvironmentRaycastHitStatus.Hit || !Finite(hit.point) || !Finite(hit.normal) ||
                    !Finite(hit.normalConfidence) || hit.normalConfidence < settings.MinimumNormalConfidence ||
                    hit.normalConfidence > 1 || hit.normal.sqrMagnitude < .0001f) continue;
                Vector3 normal = hit.normal.normalized;
                if (Vector3.Dot(normal, Vector3.up) < settings.MinimumUpDot) continue;
                float distance = Vector3.Distance(headPosition, hit.point);
                if (!Finite(distance) || distance < settings.MinimumHeadDistance || distance > settings.MaximumHeadDistance)
                    continue;
                candidates.Add(new Candidate { Index = i, Point = hit.point, Normal = normal });
                heights.Add(hit.point.y);
                if (i == centerIndex) centerValid = true;
            }

            if (!centerValid) { reason = "Center ray has no reliable horizontal surface"; return false; }
            int minimum = Math.Max(settings.MinimumHits, Mathf.CeilToInt(hits.Count * settings.MinimumInlierFraction));
            if (candidates.Count < minimum)
            { reason = "Too few reliable horizontal hits"; return false; }

            float medianHeight = Median(heights);
            var inliers = new List<Candidate>(candidates.Count);
            heights.Clear();
            centerValid = false;
            Vector3 center = Vector3.zero;
            Vector3 normalSum = Vector3.zero;
            foreach (var candidate in candidates)
            {
                if (Mathf.Abs(candidate.Point.y - medianHeight) > settings.MaximumHeightDeviation) continue;
                inliers.Add(candidate);
                heights.Add(candidate.Point.y);
                normalSum += candidate.Normal;
                if (candidate.Index == centerIndex) { center = candidate.Point; centerValid = true; }
            }
            if (!centerValid) { reason = "Center hit differs from surrounding surface"; return false; }
            if (inliers.Count < minimum)
            { reason = "Mixed heights: edge, clutter, or unstable depth"; return false; }

            heights.Sort();
            float spread = heights[heights.Count - 1] - heights[0];
            if (spread > settings.MaximumHeightSpread)
            { reason = "Surface heights vary too much"; return false; }

            Vector3 meanNormal = normalSum.normalized;
            foreach (var candidate in inliers)
            {
                if (Vector3.Dot(candidate.Normal, meanNormal) < settings.MinimumNormalAgreement)
                { reason = "Surface normals disagree"; return false; }
            }

            if (!TryValidateFootprint(inliers,settings.MinimumPatchWidth,settings.MaximumPatchDiameter,out reason))
                return false;

            float height = Median(heights);
            center.y = height;
            float verticalGap = headPosition.y - height;
            if (verticalGap < settings.MinimumVerticalGap)
            { reason = "Surface is not sufficiently below the headset"; return false; }

            estimate = new Estimate {
                Center = center, Normal = meanNormal, Height = height, HeightSpread = spread,
                HeadDistance = Vector3.Distance(headPosition, center), VerticalGap = verticalGap,
                InlierCount = inliers.Count, SampleCount = hits.Count
            };
            reason = "Horizontal surface candidate";
            return true;
        }

        // Check the actual accepted hit positions: distributed input rays can still
        // return coincident points or hits only along one edge of the surface.
        static bool TryValidateFootprint(List<Candidate> samples, float minimumWidth,
            float maximumDiameter, out string reason)
        {
            double meanX = 0, meanZ = 0;
            foreach (var sample in samples) { meanX += sample.Point.x; meanZ += sample.Point.z; }
            meanX /= samples.Count; meanZ /= samples.Count;
            double xx = 0, zz = 0, xz = 0;
            for (int i = 0; i < samples.Count; ++i)
            {
                double x = samples[i].Point.x-meanX, z = samples[i].Point.z-meanZ;
                xx += x*x; zz += z*z; xz += x*z;
                for (int j = 0; j < i; ++j)
                    if (Vector3.Distance(samples[i].Point,samples[j].Point) > maximumDiameter)
                    { reason = "Samples cover too large a patch"; return false; }
            }
            xx /= samples.Count; zz /= samples.Count; xz /= samples.Count;
            double discriminant = Math.Sqrt(Math.Max(0,(xx-zz)*(xx-zz)+4*xz*xz));
            double minorVariance = Math.Max(0,(xx+zz-discriminant)*.5);
            // Twice the minor-axis standard deviation is a conservative width indicator.
            double width = 2*Math.Sqrt(minorVariance);
            if (double.IsNaN(width) || double.IsInfinity(width) || width < minimumWidth)
            { reason = "Surface samples lack a 2D footprint"; return false; }
            reason = "Surface samples have a 2D footprint";
            return true;
        }

        static float Median(List<float> values)
        {
            values.Sort();
            int middle = values.Count / 2;
            return values.Count % 2 == 0 ? (values[middle-1] + values[middle]) * .5f : values[middle];
        }

        static bool ValidSettings(Settings s)
        {
            return s.MinimumHits >= 3 && Finite(s.MinimumInlierFraction) && s.MinimumInlierFraction > .5f && s.MinimumInlierFraction <= 1 &&
                Finite(s.MinimumUpDot) && s.MinimumUpDot > 0 && s.MinimumUpDot <= 1 &&
                Finite(s.MinimumNormalConfidence) && s.MinimumNormalConfidence >= 0 && s.MinimumNormalConfidence <= 1 &&
                Finite(s.MaximumHeightDeviation) && s.MaximumHeightDeviation > 0 &&
                Finite(s.MaximumHeightSpread) && s.MaximumHeightSpread >= 0 &&
                Finite(s.MinimumNormalAgreement) && s.MinimumNormalAgreement > 0 && s.MinimumNormalAgreement <= 1 &&
                Finite(s.MinimumPatchWidth) && s.MinimumPatchWidth > 0 &&
                Finite(s.MaximumPatchDiameter) && s.MaximumPatchDiameter > s.MinimumPatchWidth &&
                Finite(s.MinimumHeadDistance) && s.MinimumHeadDistance > 0 &&
                Finite(s.MaximumHeadDistance) && s.MaximumHeadDistance > s.MinimumHeadDistance &&
                Finite(s.MinimumVerticalGap) && s.MinimumVerticalGap >= 0;
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    }
}
