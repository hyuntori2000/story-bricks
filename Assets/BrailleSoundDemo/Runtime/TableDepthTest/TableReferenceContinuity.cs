using System;
using System.Collections.Generic;
using Meta.XR;
using UnityEngine;

namespace BrailleSoundDemo.TableDepthTest
{
    /// <summary>Bounded continuity for hand history only; never supplies capture-depth acceptance.</summary>
    public static class TableReferenceContinuity
    {
        public const double HistoryGraceSeconds = 2;
        public const double PendingLiftSeconds = .8;

        public static bool CanUseHandHistory(bool enabled, bool locked, bool headTracked,
            bool measurementFresh, bool contradicted, double verificationAgeSeconds)
        {
            if (!locked || !headTracked || contradicted) return false;
            if (measurementFresh) return true;
            return enabled && double.IsFinite(verificationAgeSeconds) &&
                verificationAgeSeconds >= 0 && verificationAgeSeconds <= HistoryGraceSeconds;
        }

        public static bool CanWaitForFreshDepth(bool enabled, bool historyUsable, bool handsValid, double pendingAgeSeconds)
            => enabled && historyUsable && handsValid && double.IsFinite(pendingAgeSeconds) &&
                pendingAgeSeconds >= 0 && pendingAgeSeconds <= PendingLiftSeconds;

        public static bool TryViewPatchCenter(Vector3 headPosition, Vector3 viewDirection,
            Vector3 lockedReference, out Vector3 center)
        {
            center = lockedReference;
            if (!Finite(headPosition) || !Finite(viewDirection) || !Finite(lockedReference) ||
                viewDirection.sqrMagnitude < .0001f || headPosition.y - lockedReference.y < .05f) return false;
            Vector3 direction = viewDirection.normalized;
            if (direction.y > -.1f) return false;
            float distance = (lockedReference.y - headPosition.y) / direction.y;
            if (!float.IsFinite(distance) || distance < .15f || distance > 3f) return false;
            Vector3 candidate = headPosition + direction * distance;
            candidate.y = lockedReference.y;
            // Nearby table work only; do not follow a grazing ray to an unrelated distant surface.
            if (!Finite(candidate) || Vector3.Distance(candidate, lockedReference) > 1f) return false;
            center = candidate;
            return true;
        }

        public static bool HasContradictoryPlane(IReadOnlyList<EnvironmentRaycastHit> hits,
            Vector3 headPosition, float lockedHeight)
        {
            if (!float.IsFinite(lockedHeight) || hits == null || hits.Count != 9) return false;
            // Require the original strong plane-estimation checks (including reliable center,
            // >=75% support and 2D footprint), not a lone raised hand or one missing depth ray.
            return TableSurfaceEstimator.TryEstimate(hits, 4, headPosition, out var estimate, out _) &&
                Mathf.Abs(estimate.Height - lockedHeight) > .04f;
        }

        static bool Finite(Vector3 point) => float.IsFinite(point.x) && float.IsFinite(point.y) && float.IsFinite(point.z);
    }
}
