namespace NINA.Plugins.AAPA {

    /// <summary>
    /// Snapshot of the AAPA device state, as returned by the :STATUS command.
    /// </summary>
    public class AAPAStatus {
        /// <summary>Azimuth motor absolute position in steps (relative to power-on or home).</summary>
        public long PositionX { get; set; }

        /// <summary>Altitude motor absolute position in steps (relative to home if homed).</summary>
        public long PositionY { get; set; }

        /// <summary>True while any axis is currently moving.</summary>
        public bool IsBusy { get; set; }

        /// <summary>True after a successful :HOMEY or :RESETY command.</summary>
        public bool IsHomed { get; set; }

        /// <summary>Minimum allowed altitude steps (soft limit).</summary>
        public long MinY { get; set; }

        /// <summary>Maximum allowed altitude steps (soft limit).</summary>
        public long MaxY { get; set; }

        public override string ToString() =>
            $"X:{PositionX} Y:{PositionY} Busy:{IsBusy} Homed:{IsHomed}";
    }
}
