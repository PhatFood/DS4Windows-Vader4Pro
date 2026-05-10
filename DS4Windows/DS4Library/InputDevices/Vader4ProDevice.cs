using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vader4ProReader.Device;
using System.Buffers.Binary;

namespace DS4Windows.InputDevices
{
    internal class Vader4ProDevice : DS4Device
    {
        private bool outputDirty = false;
        private DS4HapticState previousHapticState = new DS4HapticState();
        private readonly byte[] reportBuf = new byte[32]; // Pre-allocated to avoid per-frame heap allocation
        private long syntheticTimestampTicks = 0; // Monotonic synthetic timestamp for DS4 passthru

        private Vader4ProControllerOptions nativeOptionsStore;
        public Vader4ProControllerOptions NativeOptionsStore { get => nativeOptionsStore; }
        public Vader4ProDevice(HidDevice hidDevice, string disName, VidPidFeatureSet featureSet = VidPidFeatureSet.DefaultDS4, string macAddress = "") :
            base(hidDevice, disName, featureSet)
        {
            synced = true;
            if (macAddress != "")
            {
                Mac = macAddress;
            }
        }

        public override event ReportHandler<EventArgs> Report = null;
        public override event EventHandler BatteryChanged;
        public override event EventHandler ChargingChanged;
        public override void PostInit()
        {
            if (Mac == null || Mac == "" || Mac == BLANK_SERIAL) 
            {
                Mac = hDevice.GenerateFakeHwSerial();
            }
            deviceType = InputDeviceType.Vader4Pro;
            gyroMouseSensSettings = new GyroMouseSens();
            inputReport = new byte[hDevice.Capabilities.InputReportByteLength];
            warnInterval = WARN_INTERVAL_USB;
            conType = ConnectionType.USB;
            byte[] buf = new byte[32];
            buf[0] = 0x05;
            buf[1] = 0x10;
            buf[2] = 0x01;
            buf[3] = 0x01;
            buf[4] = 0x01;
            hDevice.WriteOutputReportViaInterrupt(buf, READ_STREAM_TIMEOUT);
        }

        public override bool DisconnectBT(bool callRemoval = false)
        {
            // Do Nothing
            return true;
        }

        public override bool DisconnectDongle(bool remove = false)
        {
            // Do Nothing
            return true;
        }

        public override bool DisconnectWireless(bool callRemoval = false)
        {
            return true;
        }

        public override bool IsAlive()
        {
            return synced;
        }

        public override void RefreshCalibration()
        {
            return;
        }

        public override void StartUpdate()
        {
            this.inputReportErrorCount = 0;

            if (ds4Input == null)
            {
                ds4Input = new Thread(ReadInput);
                ds4Input.Priority = ThreadPriority.AboveNormal;
                ds4Input.Name = "Vader 4 Pro Input thread: " + Mac;
                ds4Input.IsBackground = true;
                ds4Input.Start();
            }
            else
                Console.WriteLine("Thread already running for Vader 4 Pro: " + Mac);
        }

        private unsafe void ReadInput()
        {
            unchecked
            {
                Debouncer = SetupDebouncer();
                firstActive = DateTime.UtcNow;
                NativeMethods.HidD_SetNumInputBuffers(hDevice.SafeReadHandle.DangerousGetHandle(), 2);
                Queue<long> latencyQueue = new Queue<long>(21); // Set capacity at max + 1 to avoid any resizing
                int tempLatencyCount = 0;
                long oldtime = 0;
                string currerror = string.Empty;
                long curtime = 0;
                long testelapsed = 0;
                timeoutEvent = false;
                ds4InactiveFrame = true;
                idleInput = true;
                bool syncWriteReport = conType != ConnectionType.BT;

                bool tempCharging = charging;
                double elapsedDeltaTime = 0.0;
                long latencySum = 0;

                // Let DS4Windows' own continuous calibration run (started in DS4SixAxis constructor).
                // The Vader 4 Pro firmware has unreliable built-in auto-calibration that can cause
                // drift and offset issues. DS4Windows' calibration provides a secondary correction.
                standbySw.Start();

                while (!exitInputThread)
                {
                    oldCharging = charging;
                    currerror = string.Empty;

                    if (tempLatencyCount >= 20)
                    {
                        latencySum -= latencyQueue.Dequeue();
                        tempLatencyCount--;
                    }

                    latencySum += this.lastTimeElapsed;
                    latencyQueue.Enqueue(this.lastTimeElapsed);
                    tempLatencyCount++;

                    //Latency = latencyQueue.Average();
                    Latency = latencySum / (double)tempLatencyCount;

                    readWaitEv.Set();

                    HidDevice.ReadStatus res = hDevice.ReadFile(inputReport);
                    if (res != HidDevice.ReadStatus.Success)
                    {

                        exitInputThread = true;
                        readWaitEv.Reset();
                        StopOutputUpdate();
                        isDisconnecting = true;
                        RunRemoval();
                        timeoutExecuted = true;
                        continue;
                    }
                    readWaitEv.Wait();
                    readWaitEv.Reset();

                    curtime = Stopwatch.GetTimestamp();
                    testelapsed = curtime - oldtime;
                    lastTimeElapsedDouble = testelapsed * (1.0 / Stopwatch.Frequency) * 1000.0;
                    lastTimeElapsed = (long)lastTimeElapsedDouble;
                    oldtime = curtime;

                    utcNow = DateTime.UtcNow; // timestamp with UTC in case system time zone changes

                    cState.PacketCounter = pState.PacketCounter + 1;
                    cState.ReportTimeStamp = utcNow;
                    int copyLen = Math.Min(inputReport.Length, 32); // safe length
                    Array.Copy(inputReport, reportBuf, copyLen);   // reuse pre-allocated buffer
                    if (reportBuf[1] == 0xFE)
                    {
                        var report = new Vader4ProReport(reportBuf);
                        // Dpad
                        cState.DpadUp = report.IsDPadUpPressed;
                        cState.DpadRight = report.IsDPadRightPressed;
                        cState.DpadDown = report.IsDPadDownPressed;
                        cState.DpadLeft = report.IsDPadLeftPressed;
                        // Left Stick
                        cState.LX = report.LS_X;
                        cState.LY = report.LS_Y;
                        cState.L3 = report.IsLSPressed;
                        // Right Stick
                        cState.RX = report.RS_X;
                        cState.RY = report.RS_Y;
                        cState.R3 = report.IsRSPressed;
                        // Share/Options
                        cState.Share = report.IsSelectPressed;
                        cState.Options = report.IsStartPressed;
                        // Left Bumper / Right Bumper
                        cState.L1 = report.IsLBPressed;
                        cState.R1 = report.IsRBPressed;
                        // Left Trigger
                        cState.L2 = report.LT;
                        cState.L2Btn = cState.L2 > 0;
                        cState.L2Raw = cState.L2;
                        // Right Trigger
                        cState.R2 = report.RT;
                        cState.R2Btn = cState.R2 > 0;
                        cState.R2Raw = cState.R2;
                        // Face Buttons
                        cState.Cross = report.IsAPressed;
                        cState.Circle = report.IsBPressed;
                        cState.Square = report.IsXPressed;
                        cState.Triangle = report.IsYPressed;
                        // PlayStation Button
                        cState.PS = report.IsHOMEPressed;
                        // Paddles
                        cState.SideL = report.IsM4Pressed;
                        cState.SideR = report.IsM3Pressed;
                        cState.BLP = report.IsM2Pressed;
                        cState.BRP = report.IsM1Pressed;
                        // C,Z buttons
                        cState.FnL = report.IsCPressed;
                        cState.FnR = report.IsZPressed;
                        // FN button
                        cState.Capture = report.IsFNPressed;
                        // Gyro / Accelerometer
                        short yaw = (short)-report.YawCalibrated;
                        short pitch = (short)-report.PitchCalibrated;
                        short roll = (short)(report.RollCalibrated);
                        short ax = (short)-(report.AccelXCalibrated);
                        short ay = (short)(report.AccelYCalibrated);
                        short az = (short)(report.AccelZCalibrated);
                        if (synced)
                        {
                            sixAxis.handleSixaxisVals(yaw, pitch, roll, ax, ay, az, cState, elapsedDeltaTime);
                        }
                    }

                    cState.Battery = 99;


                    // Vader 4 Pro has no hardware timestamp in its HID report.
                    // Generate a synthetic monotonic timestamp from the system high-res timer
                    // so DS4 Passthru games see a properly advancing timestamp field.
                    elapsedDeltaTime = lastTimeElapsedDouble * .001; // ms → seconds
                    uint elapsedMicro = (uint)(lastTimeElapsedDouble * 1000.0); // ms → microseconds
                    cState.totalMicroSec = pState.totalMicroSec + elapsedMicro;
                    cState.elapsedTime = elapsedDeltaTime;

                    // Advance synthetic timestamp (~5.33 µs per tick, matching DS4 convention)
                    syntheticTimestampTicks += elapsedMicro * 3; // microseconds * 3 to match DS4's 1/3µs tick rate
                    cState.ds4Timestamp = (ushort)((syntheticTimestampTicks / 16) % ushort.MaxValue);


                    if (idleTimeout == 0)
                    {
                        lastActive = utcNow;
                    }
                    else
                    {
                        idleInput = isDS4Idle();
                        if (!idleInput)
                        {
                            lastActive = utcNow;
                        }
                    }

                    if (fireReport)
                    {
                        Report?.Invoke(this, EventArgs.Empty);
                    }

                    PrepareOutReport();
                    if (outputDirty)
                    {
                        WriteReport();
                        previousHapticState = currentHap;
                    }

                    currentHap.dirty = false;
                    outputDirty = false;


                    if (!string.IsNullOrEmpty(currerror))
                        error = currerror;
                    else if (!string.IsNullOrEmpty(error))
                        error = string.Empty;

                    cState.CopyTo(pState);

                    if (hasInputEvts)
                    {
                        lock (eventQueueLock)
                        {
                            Action tempAct = null;
                            for (int actInd = 0, actLen = eventQueue.Count; actInd < actLen; actInd++)
                            {
                                tempAct = eventQueue.Dequeue();
                                tempAct.Invoke();
                            }

                            hasInputEvts = false;
                        }
                    }
                }

            }
            timeoutExecuted = true;
        }


        private void PrepareOutReport()
        {
            MergeStates();
            bool change = false;
            bool rumbleSet = currentHap.IsRumbleSet();
            if (currentHap.dirty || !previousHapticState.Equals(currentHap))
            {
                change = true;
            }
            if (change)
            {
                outputDirty = true;
                if (rumbleSet)
                {
                    standbySw.Restart();
                }
                else
                {
                    standbySw.Reset();
                }
            }
            else if (rumbleSet && standbySw.ElapsedMilliseconds >= 4000L)
            {
                outputDirty = true;
                standbySw.Restart();
            }
        }
        private void WriteReport()
        {
            SetLed(currentHap.lightbarState.LightBarColor.red, currentHap.lightbarState.LightBarColor.green, currentHap.lightbarState.LightBarColor.blue);
            SetRumble(currentHap.rumbleState.RumbleMotorStrengthLeftHeavySlow, currentHap.rumbleState.RumbleMotorStrengthRightLightFast);
        }
        private void SetRumble(byte leftMain, byte rightMain, byte leftTrigger = 0, byte rightTrigger = 0)
        {
            byte[] buf = new byte[32];
            buf[0] = 0x05;
            buf[1] = 0x0F;
            buf[2] = leftMain;
            buf[3] = rightMain;
            buf[4] = leftTrigger;
            buf[5] = rightTrigger;
            hDevice.WriteOutputReportViaInterrupt(buf, READ_STREAM_TIMEOUT);
        }

        private void SetLed(byte r, byte g, byte b)
        {
            byte[] buf = new byte[32];
            buf[0] = 0x05;
            buf[1] = 0xE0;
            buf[2] = r;
            buf[3] = g;
            buf[4] = b;
            hDevice.WriteOutputReportViaInterrupt(buf, READ_STREAM_TIMEOUT);
        }

        protected override void StopOutputUpdate()
        {
            byte[] buf = new byte[32];
            buf[0] = 0x05;
            buf[1] = 0x10;
            buf[2] = 0x01;
            buf[3] = 0x01;
            buf[4] = 0x01;
            hDevice.WriteOutputReportViaInterrupt(buf, READ_STREAM_TIMEOUT);
            SetLed(0, 0, 0);
            setRumble(0, 0);
        }

    }
}
