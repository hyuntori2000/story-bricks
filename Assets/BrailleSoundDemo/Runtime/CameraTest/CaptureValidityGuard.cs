using System;

namespace BrailleSoundDemo.CameraTest
{
    /// <summary>
    /// Live capture gates protect frame acquisition. An opt-in frozen-image gate
    /// protects only that image's session/reference while it is transported.
    /// With no frozen gate, legacy live validation continues through the ACK.
    /// </summary>
    public sealed class CaptureValidityGuard
    {
        readonly Func<bool> liveValid, frozenContextValid;
        public bool SnapshotFrozen { get; private set; }

        public CaptureValidityGuard(Func<bool> liveValid, Func<bool> frozenContextValid = null)
        { this.liveValid = liveValid; this.frozenContextValid = frozenContextValid; }

        public bool CanContinue(bool operationCurrent, bool cameraFresh)
        {
            if (!operationCurrent) return false;
            return SnapshotFrozen && frozenContextValid != null
                ? Check(frozenContextValid)
                : cameraFresh && Check(liveValid);
        }

        /// <summary>Call only after successful readback, metadata validation and private-image encoding.</summary>
        public bool TryFreeze(bool operationCurrent, bool cameraFresh)
        {
            if (SnapshotFrozen) return CanContinue(operationCurrent, cameraFresh);
            if (!operationCurrent || !cameraFresh || !Check(liveValid) ||
                (frozenContextValid != null && !Check(frozenContextValid))) return false;
            SnapshotFrozen = true;
            return true;
        }

        static bool Check(Func<bool> predicate)
        {
            try { return predicate != null && predicate(); }
            catch { return false; }
        }
    }
}
