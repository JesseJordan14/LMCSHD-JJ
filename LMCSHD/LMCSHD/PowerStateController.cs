using Microsoft.Win32;

namespace LMCSHD
{
    // Watches Windows session and power events to drive the wall on/off
    // lifecycle (Feature 6.2). The wall is "Active" only when the session is
    // unlocked AND the PC is awake. Either condition flipping puts us into
    // Idle (NetworkManager pushes blank frames).
    //
    // Tracking session and power as two independent flags handles cases the
    // single-flag approach gets wrong:
    //   - PC sleeps while unlocked, then wakes to a lock screen ("require
    //     sign-in on wake"): Resume fires before SessionUnlock. With one
    //     flag, the wall would briefly light up over the lock screen. With
    //     two flags, _sessionUnlocked stays false until SessionUnlock fires.
    //   - User locks (Win+L) without sleeping: SessionLock fires, no power
    //     event. Wall blanks. Unlock alone re-Activates.
    //
    // Belt-and-suspenders with the firmware-side blank-on-disconnect (6.1):
    // SessionLock fires BEFORE the WS connection drops, so the wall blanks
    // instantly. The firmware path covers hard cases (LMCSHD crash, OS
    // killing the process during sleep) where we can't push proactively.
    //
    // Per the user's 2026-04-29 decisions: Lock + Sleep + Shutdown all
    // blank for now. Per-event customization (e.g. "lock doesn't blank")
    // is a deliberate Feature 6 follow-up — see PROJECT.md.
    public static class PowerStateController
    {
        private static bool _started = false;
        private static bool _sessionUnlocked = true;
        private static bool _powerAwake = true;

        public static void Start()
        {
            if (_started) return;
            _started = true;

            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            // App is starting; user is logged in by definition. Both flags
            // start true, sync NetworkManager to match.
            ApplyState();
        }

        public static void Stop()
        {
            if (!_started) return;
            _started = false;

            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }

        private static void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock:
                case SessionSwitchReason.SessionLogoff:
                    _sessionUnlocked = false;
                    ApplyState();
                    break;
                case SessionSwitchReason.SessionUnlock:
                case SessionSwitchReason.SessionLogon:
                    _sessionUnlocked = true;
                    ApplyState();
                    break;
            }
        }

        private static void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            switch (e.Mode)
            {
                case PowerModes.Suspend:
                    _powerAwake = false;
                    ApplyState();
                    break;
                case PowerModes.Resume:
                    _powerAwake = true;
                    ApplyState();
                    break;
            }
        }

        private static void ApplyState()
        {
            NetworkManager.SetActive(_sessionUnlocked && _powerAwake);
        }
    }
}
