using ElectronNET.API;
using ElectronNET.API.Entities;
using log4net;
using R3E;
using R3E.Data;
using ReHUD.Extensions;
using ReHUD.Interfaces;
using ReHUD.Models;
using ReHUD.Models.LapData;
using ReHUD.Utils;

namespace ReHUD.Services
{
    public class R3EDataService : IR3EDataService, IDisposable
    {
        public static readonly ILog logger = LogManager.GetLogger(typeof(R3EDataService));

        private Shared data;
        private R3EExtraData extraData;

        private readonly IEventService eventService;
        private readonly ILapDataService lapDataService;
        private readonly IRaceRoomObserver raceRoomObserver;
        private readonly ISharedMemoryService sharedMemoryService;
        private readonly IDriverService driverService;

        private readonly AutoResetEvent resetEvent;
        private CancellationTokenSource cancellationTokenSource = new();

        private volatile bool _isRunning = false;
        public bool IsRunning { get => _isRunning; }

        public R3EExtraData Data { get => extraData; }

        private BrowserWindow window;
        public BrowserWindow HUDWindow { get => window; set => window = value; }
        private bool? hudShown = false;
        public bool? HUDShown { get => hudShown; set => hudShown = value; }
        private string[]? usedKeys;
        public string[]? UsedKeys { get => usedKeys; set => usedKeys = value; }

        private bool enteredEditMode = false;
        private bool recordingData = false;
        private bool lapValid = false;
        private DateTime lastLapInvalidation = DateTime.MinValue;
        private float? lastFuel = null;
        private float? lastFuelUsage = null;
        private TireWearObj? lastTireWear = null;
        private TireWearObj? lastTireWearDiff = null;
        private int? lastLapNum = null;

        public R3EDataService(IEventService eventService, IRaceRoomObserver raceRoomObserver, ISharedMemoryService sharedMemoryService, IDriverService driverService)
        {
            this.eventService = eventService;
            this.lapDataService = new LapDataService();
            this.raceRoomObserver = raceRoomObserver;
            this.sharedMemoryService = sharedMemoryService;
            this.driverService = driverService;

            extraData = new()
            {
                forceUpdateAll = false
            };

            resetEvent = new AutoResetEvent(false);

            this.raceRoomObserver.OnProcessStarted += RaceRoomStarted;
            this.raceRoomObserver.OnProcessStopped += RaceRoomStopped;

            this.sharedMemoryService.OnDataReady += OnDataReady;
            this.eventService.NewLap += SaveData;
            this.eventService.PositionJump += InvalidateLapDriver;
            this.eventService.EnterPitlane += InvalidateLapDriver;
            this.eventService.ExitPitlane += InvalidateLapDriver;
            this.eventService.SessionChange += (sender, e) => InvalidateLap();
            this.eventService.EnterReplay += (sender, e) => InvalidateLap();
            this.eventService.ExitReplay += (sender, e) => InvalidateLap();
            this.eventService.MainDriverChange += (sender, e) =>
            {
                InvalidateLap();
                driverService.UpdateBestLap(LoadBestLap());
            };
        }

        public void Dispose()
        {
            cancellationTokenSource.Cancel();

            raceRoomObserver.OnProcessStarted -= RaceRoomStarted;
            raceRoomObserver.OnProcessStopped -= RaceRoomStopped;

            resetEvent.Dispose();
        }

        private void RaceRoomStarted()
        {
            logger.Info("RaceRoom started, starting R3EDataService worker");

            cancellationTokenSource.Cancel();
            cancellationTokenSource.Dispose();

            cancellationTokenSource = new();
            Task.Run(() => ProcessR3EData(cancellationTokenSource.Token), cancellationTokenSource.Token);
        }

        private void RaceRoomStopped()
        {
            logger.Info("RaceRoom stopped, stopping R3EDataService worker");

            cancellationTokenSource.Cancel();
            _isRunning = false;
        }

        private void OnDataReady(Shared data)
        {
            this.data = data;
            resetEvent.Set();
        }


        private void SetRecordingData()
        {
            if (!recordingData)
            {
                logger.Info("Recording data");
                recordingData = true;
            }
        }

        private void ResetRecordingData()
        {
            if (recordingData)
            {
                logger.Info("Stopped recording data");
                recordingData = false;
                lapValid = false;
            }
        }


        private void ForceSetLapValid()
        {
            if (!lapValid)
            {
                logger.Info("Setting lap valid");
                lapValid = true;
            }
        }

        private void GraceSetLapValid()
        {
            if (DateTime.UtcNow - lastLapInvalidation < TimeSpan.FromSeconds(5))
            {
                ForceSetLapValid();
            }
        }

        private void ResetLapValid()
        {
            if (lapValid)
            {
                logger.Info("Setting lap invalid");
                lapValid = false;
                lastLapInvalidation = DateTime.UtcNow;
            }
        }

        private void InvalidateLap()
        {
            ResetRecordingData();
            ResetLapValid();
        }

        private void InvalidateLapDriver(object? sender, DriverEventArgs driver)
        {
            if (driver.IsMainDriver)
            {
                InvalidateLap();
            }
        }

        public static readonly TimeSpan UPDATE_COMBINATION_SUMMARY_EVERY = TimeSpan.FromMilliseconds(300);

        private async Task ProcessR3EData(CancellationToken cancellationToken)
        {
            logger.Info("Starting R3EDataService worker");

            var lastCombinationSummaryUpdate = DateTime.UtcNow - TimeSpan.FromMinutes(1);
            while (!cancellationToken.IsCancellationRequested)
            {
                resetEvent.WaitOne();
                resetEvent.Reset();

                extraData.timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();


                var dataClone = data; // Clone the struct to avoid collisions with the shared memory service.

                // Bug in shared memory, sometimes numCars is updated in delay, this partially fixes it.
                dataClone.NumCars = Math.Max(dataClone.NumCars, dataClone.Position);
                var firstEmptyIndex = Array.FindIndex(dataClone.DriverData, d => d.DriverInfo.SlotId == -1);
                if (firstEmptyIndex != -1)
                {
                    dataClone.NumCars = Math.Min(dataClone.NumCars, firstEmptyIndex);
                }
                dataClone.DriverData = dataClone.DriverData.Take(dataClone.NumCars).ToArray();
                var utcNow = DateTime.UtcNow;
                if (dataClone.LayoutId != -1 && dataClone.VehicleInfo.ModelId != -1 && utcNow - lastCombinationSummaryUpdate > UPDATE_COMBINATION_SUMMARY_EVERY)
                {
                    var tireSubtypeFront = (Constant.TireSubtype)dataClone.TireSubtypeFront;
                    var tireSubtypeRear = (Constant.TireSubtype)dataClone.TireSubtypeRear;
                    lastCombinationSummaryUpdate = utcNow;
                    CombinationSummary combination = lapDataService.GetCombinationSummary(dataClone.LayoutId, dataClone.VehicleInfo.ModelId, tireSubtypeFront, tireSubtypeRear);
                    extraData.fuelPerLap = combination.AverageFuelUsage;
                    extraData.fuelLastLap = lastFuelUsage;
                    extraData.tireWearPerLap = combination.AverageTireWear;
                    extraData.tireWearLastLap = lastTireWearDiff;
                    extraData.averageLapTime = combination.AverageLapTime;
                    Tuple<int?, double?> lapData = Utilities.GetEstimatedLapCount(dataClone, combination.BestLapTime);
                    extraData.estimatedRaceLapCount = lapData.Item1;
                    extraData.lapsUntilFinish = lapData.Item2;
                    var bestLap = lapDataService.GetCarBestLap(dataClone.LayoutId, dataClone.VehicleInfo.ModelId, tireSubtypeFront, tireSubtypeRear);
                    if (bestLap == null)
                    {
                        extraData.allTimeBestLapTime = null;
                    }
                    else
                    {
                        extraData.allTimeBestLapTime = bestLap.LapTime.Value;
                    }
                }
                extraData.rawData = dataClone;

                if (dataClone.GameInReplay == 1 || dataClone.InPitlane == 1)
                {
                    ResetRecordingData();
                }

                if (data.CurrentLapValid == 0)
                {
                    ResetLapValid();
                }
                else
                {
                    // currentLapValid is sometimes updated after the new lap event, so we need to be able to re-validate it.
                    GraceSetLapValid();
                }

                try
                {
                    extraData.events = eventService.Cycle(dataClone);
                }
                catch (Exception e)
                {
                    logger.Error("Error in event cycle", e);
                }

                try
                {
                    extraData = driverService.ProcessExtraData(extraData);
                }
                catch (Exception e)
                {
                    logger.Error("Error in driver service", e);
                }


                if (window != null)
                {
                    if (enteredEditMode)
                    {
                        extraData.forceUpdateAll = true;
                        await IpcCommunication.Invoke(window, "r3eData", extraData.Serialize(usedKeys));
                        extraData.forceUpdateAll = false;
                        enteredEditMode = false;
                    }
                    else
                    {
                        await IpcCommunication.Invoke(window, "r3eData", extraData.Serialize(usedKeys));
                    }
                }

                if (dataClone.IsInMenus())
                {
                    if (window != null && (hudShown ?? true))
                    {
                        Electron.IpcMain.Send(window, "hide");
                        hudShown = false;
                    }

                    if (dataClone.SessionType == -1)
                    {
                        lastLapNum = null;
                        lastFuel = null;
                        lastTireWear = null;
                    }
                }
                else if (window != null && !(hudShown ?? false))
                {
                    Electron.IpcMain.Send(window, "show");
                    window.SetAlwaysOnTop(!Startup.IsInVrMode, OnTopLevel.screenSaver);
                    hudShown = true;
                }

                lastLapNum = dataClone.CompletedLaps;
            }

            logger.Info("R3EDataService worker thread stopped");
        }

        private static TireWearObj AsTireWear(TireData<float> tireWear)
        {
            return new TireWearObj
            {
                FrontLeft = tireWear.FrontLeft,
                FrontRight = tireWear.FrontRight,
                RearLeft = tireWear.RearLeft,
                RearRight = tireWear.RearRight,
            };
        }

        private void SaveData(object? sender, DriverEventArgs args)
        {
            Tuple<Driver, bool>? res = driverService.NewLap(extraData, args.Driver);

            if (!args.IsMainDriver || res == null)
            {
                return;
            }

            float? fuelNow = data.VehicleInfo.EngineType == 1 ? data.BatterySoC : data.FuelLeft;
            fuelNow = fuelNow == -1 ? null : fuelNow;
            TireWearObj tireWearNow = AsTireWear(data.TireWear);
            bool lapSaved = false;

            try
            {
                double? laptime = res.Item1.GetLastLaptime();
                if (laptime == null)
                {
                    logger.Error("Last lap was abnormal, not saving lap");
                    return;
                }

                if (recordingData)
                {
                    bool tireWearDataValid = lastTireWear != null && tireWearNow != null;
                    TireWearObj? tireWearDiff = tireWearDataValid ? lastTireWear! - tireWearNow! : null;

                    bool fuelDataValid = lastFuel != null && fuelNow != -1;
                    float? fuelDiff = fuelDataValid ? lastFuel - fuelNow : null;

                    if (lapValid)
                    {
                        LapContext context = new(data.LayoutId, data.VehicleInfo.ModelId, data.VehicleInfo.ClassPerformanceIndex, data.TireSubtypeFront, data.TireSubtypeRear);
                        if (laptime != null)
                        {
                            var savedTransaction = false;
                            var transaction = lapDataService.BeginTransaction();
                            try
                            {
                                Lap lap = lapDataService.LogLap(context, lapValid, laptime.Value);
                                lapSaved = true;
                                extraData.lapId = lap.Id;
                                extraData.lastLapTime = laptime;

                                if (tireWearDataValid && data.TireWearActive >= 1)
                                {
                                    lapDataService.Log(new TireWear(lap, tireWearDiff!, new TireWearContext(data.TireWearActive)));
                                }

                                if (fuelDataValid && data.FuelUseActive >= 1)
                                {
                                    lapDataService.Log(new FuelUsage(lap, fuelDiff!.Value, new FuelUsageContext(data.FuelUseActive)));
                                }

                                if (res.Item2)
                                {
                                    SaveBestLap(lap, res.Item1.BestLap!.GetNonNullPoints(), Driver.DATA_POINTS_GAP);
                                }

                                // Commit async to not block the thread.
                                // This is safe because this method is only called when a lap is completed, so there's plenty of time between calls.
                                // TODO: Maybe move this to a separate thread with a queue?
                                transaction.CommitAsync();
                                savedTransaction = true;
                            }
                            catch (Exception e)
                            {
                                logger.Error("Error saving lap", e);
                            }
                            finally
                            {
                                if (!savedTransaction)
                                {
                                    transaction.Rollback();
                                }
                                transaction.Dispose();
                            }
                        }
                        else
                        {
                            logger.Error("No valid laptime found, not saving lap");
                            extraData.lapId = null;
                        }
                    }
                    else
                    {
                        logger.Info("Lap not valid, not saving lap");
                    }

                    if (data.TireWearActive >= 1)
                    {
                        lastTireWearDiff = tireWearDiff;
                    }
                    if (data.FuelUseActive >= 1)
                    {
                        lastFuelUsage = fuelDiff;
                    }
                }
            }
            finally
            {
                if (data.IsDriving())
                {
                    // First race lap should not be saved because it starts from the grid.
                    if (data.SessionType != (int)Constant.Session.Race || data.CompletedLaps > 0)
                    {
                        SetRecordingData();
                    }
                    ForceSetLapValid();
                    lastFuel = fuelNow;
                    lastTireWear = tireWearNow;
                }

                if (!lapSaved)
                {
                    extraData.lapId = null;
                }
            }
        }

        public void SetEnteredEditMode()
        {
            enteredEditMode = true;
        }

        public void SaveBestLap(Lap lap, double[] points, int pointsGap)
        {
            logger.InfoFormat("SaveBestLap: lapId={0}, trackLayoutId={1}, carId={2}, classPerformanceIndex={3}", lap.Id, lap.Context.TrackLayoutId, lap.Context.CarId, lap.Context.ClassPerformanceIndex);

            Telemetry telemetry = new(lap, new(points, pointsGap));
            lapDataService.Log(telemetry);
        }

        public Lap? LoadBestLap()
        {
            var layoutId = data.LayoutId;
            var carId = data.VehicleInfo.ModelId;
            var classPerformanceIndex = data.VehicleInfo.ClassPerformanceIndex;
            var tireSubtypeFront = (Constant.TireSubtype)data.TireSubtypeFront;
            var tireSubtypeRear = (Constant.TireSubtype)data.TireSubtypeRear;

            var innerLapDataService = new LapDataService(); // Create a new one because we're in a different thread
            return innerLapDataService.GetClassBestLap(layoutId, carId, classPerformanceIndex, tireSubtypeFront, tireSubtypeRear);
        }

        public async Task SendEmptyData()
        {
            logger.Info("Sending empty data for edit mode");
            var emptyData = R3EExtraData.NewEmpty();
            await IpcCommunication.Invoke(window, "r3eData", emptyData.Serialize(usedKeys));
        }
    }
}
