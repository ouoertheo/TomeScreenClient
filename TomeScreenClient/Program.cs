// Audio Detection https://stackoverflow.com/questions/6616227/how-do-i-figure-out-if-windows-is-currently-playing-any-sounds
// Icons made by <a href="https://www.flaticon.com/authors/freepik" title="Freepik">Freepik</a> from <a href="https://www.flaticon.com/" title="Flaticon"> www.flaticon.com</a>
// Workstation lock detection http://omegacoder.com/?p=516
// Error handling https://stackify.com/csharp-catch-all-exceptions/
using System;
using System.Windows.Forms;
using CSCore.CoreAudioAPI;
using System.Text.Json;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Configuration;
using Microsoft.Win32;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Diagnostics;

namespace TomeScreenClient
{
    static class Program
    {

        //
        [DllImport("user32.dll")]
        public static extern bool LockWorkStation();
        // Timer fields
        private static System.Timers.Timer timer;
        private static int interval = 5000;

        // Lock event fields
        private static SessionSwitchEventHandler sseh;
        private static bool sessionLocked = false;

        // Other fields
        private static string timeServerURL;
        private static float audioVolumeThreshold;
        private static List<float> audioVolumeTracker = new List<float>();
        private static bool breakNotification = false;

        public static int LogLevel { get; set; }

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += new ThreadExceptionEventHandler(Application_ThreadException);
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);

            // Get initial settings
            var appSettings = ConfigurationManager.AppSettings;
            try
            {
                interval = Convert.ToInt32(appSettings["interval"]);
                FileLogger.Log("Using interval from app.config: " + interval, 1);
            }
            catch
            {
                interval = 60000;
                FileLogger.Log("Using default interval of 60000: " + interval, 1);
            }

            try
            {
                timeServerURL = appSettings["TimeServerURL"];
                FileLogger.Log("Using " + timeServerURL + " from App.Config", 1);
            }
            catch
            {

                timeServerURL = "http://timeserver.tomeofjamin.net:20145";
                FileLogger.Log("Issue in App.Config entry for <add key=\"TimeServerURL\" value=\"http://timeserverurl\"/>. Using " + timeServerURL, 1);
            }
            try
            {
                LogLevel = Convert.ToInt32(appSettings["LogLevel"]);
                FileLogger.Log("Using LogLevel from app.config: " + LogLevel, 1);
            }
            catch
            {
                LogLevel = 1; // Default log level
                FileLogger.Log("Using default log level: " + LogLevel, 1);
            }
            try
            {
                audioVolumeThreshold = Convert.ToInt32(appSettings["audioVolumeThreshold"]);
                FileLogger.Log("Using audioVolumeThreshold from app.config: " + audioVolumeThreshold, 1);
            }
            catch
            {
                audioVolumeThreshold = 0.005f;
                FileLogger.Log("Using default audioVolumeThreshold: " + audioVolumeThreshold, 1);
            }

            httpHandler.setTimeServerUrl(timeServerURL);

            // Set up a timer that triggers every minute.
            timer = new System.Timers.Timer();
            timer.Interval = interval;
            timer.Elapsed += OnTimer;
            timer.Start();

            // Register event for when user locks the system
            sseh = new SessionSwitchEventHandler(SysEventsCheck);
            SystemEvents.SessionSwitch += sseh;

            // Use application context so we can exist only in the system tray

            Application.Run(new MyCustomApplicationContext());
        }

        static void Application_ThreadException(object sender, ThreadExceptionEventArgs e)
        {
            // Log the exception, display it, etc
            FileLogger.Log(e.Exception.Message,1);
        }

        static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            // Log the exception, display it, etc
            FileLogger.Log((e.ExceptionObject as Exception).Message,1);
        }

        // Every interval, if system is not idle, post interval time to the server.
        private static void OnTimer(object source, System.Timers.ElapsedEventArgs arg)
        {


            if (!sessionLocked)
            {
                long idleTime = 0;
                string userName = Environment.UserName;
                //string userName = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
                string hostName = Environment.MachineName;

                // Get duration where user has no input (idle time)
                try
                {
                    idleTime = IdleTime.IdleTime.GetIdleTime();
                }
                catch (Exception e)
                {
                    FileLogger.Log(e.Message, 1);
                }

                // Check if any audio is playing, which is used to indicate user is consuming media
                // In that case, poll the server with idle value 0, because we assume the user is not idle.
                if (IsAudioPlaying(GetDefaultRenderDevice()))
                {
                    FileLogger.Log("User is active with audio stream. Polling to server", 1);
                    Activity thisActivity = new Activity(userName, hostName, DateTime.Now, Activity.activityType.idle, interval);
                    try
                    {
                        sendActivity(thisActivity);
                        MyCustomApplicationContext.noError();
                    }
                    catch
                    {
                        MyCustomApplicationContext.setError();
                    }
                }
                // If the idle time is more than the interval, then we can assume that the user is not active
                else if (idleTime < interval)
                // Poll only if idle time is less than the defined poll interval
                {
                    Activity thisActivity = new Activity(userName, hostName, DateTime.Now, Activity.activityType.idle, interval);
                    try
                    {
                        sendActivity(thisActivity);
                        MyCustomApplicationContext.noError();
                    }
                    catch
                    {
                        MyCustomApplicationContext.setError();
                    }

                }
                else
                // No need to send a poll if we are idle > 2x polling interval
                {
                    FileLogger.Log("Ignoring poll, idleTime: " + idleTime, 1);
                }
            }
            else
            {
                FileLogger.Log("Session is locked, skipped polling", 1);
            }
        }

        // Handle the lock event, setting the sessionLocked field to true/false if the system locks, unlocks
        private static void SysEventsCheck(object sender, SessionSwitchEventArgs e)
        {
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock: FileLogger.Log("Lock Encountered", 1); sessionLocked = true; break;
                case SessionSwitchReason.SessionUnlock: FileLogger.Log("UnLock Encountered", 1); sessionLocked = false; break;
            }
        }

        // Handle checking if audio is playing
        public static MMDevice GetDefaultRenderDevice()
        {
            using (var enumerator = new MMDeviceEnumerator())
            {
                return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            }
        }

        public static bool IsAudioPlaying(MMDevice device)
        {
            // Value ranges: 
            // .3 to .6 is listening to music from an app at high volume.
            // .06 to .07 will be that same music at half the app volume level.
            // ~.022 will be that same music at quarter app volume level.
            // ~.007 will be that same music at 1/8th app volume level.
            // ~.001 will be barely audible.
            // 
            // Take the average of the last 5 audio level samples and make sure 
            // audio levels are consistently over the threshold
            //
            // Just to make things more complicated, for the user to actually be 
            // listening to something, then the master volume should be at a decent
            // level. So at half level (0.6 to 0.7), and a .2 system volume level, 
            // we are roughly at the .005 to .001 range of actual hearing levels. 
            // rather than do the math, i opted for manually setting those by halving
            // audioVolumeThreshold when master volume < .5

            using (var meter = AudioMeterInformation.FromDevice(device))
            {
                var masterVolume = AudioEndpointVolume.FromDevice(device).GetMasterVolumeLevelScalar();
                float modifiedAudioVolumeThreshold = audioVolumeThreshold;
                // Look at last 5 samples and average them. 
                audioVolumeTracker.Add(meter.PeakValue);
                if (audioVolumeTracker.Count > 5)
                {
                    audioVolumeTracker.RemoveAt(0);
                }
                float audioVolumeAverage = audioVolumeTracker.Average();

                if (masterVolume < .50)
                {
                    modifiedAudioVolumeThreshold = modifiedAudioVolumeThreshold * 1.5f;
                }

                FileLogger.Log("-----------------+ Audio Debug Logs +----------------------------", 2);
                FileLogger.Log("Audio Meter Value  : " + meter.PeakValue, 2);
                FileLogger.Log("Audio Meter Average: " + audioVolumeAverage, 2);
                FileLogger.Log("Master Volume Level: " + masterVolume, 2);
                FileLogger.Log("Modified Threshold: " + modifiedAudioVolumeThreshold, 2);

                return audioVolumeAverage > modifiedAudioVolumeThreshold;
            }
        }



        // Trigger REST endpoint activities      
        private async static void sendActivity(Activity thisActivity)
        {
            FileLogger.Log("This activity:", 1);
            FileLogger.Log(JsonSerializer.Serialize(thisActivity), 1);
            FileLogger.Log("Calling httpHandler", 3);
            try{
                var response = await httpHandler.postToTimeServerAsync(thisActivity);
                try { 
                    getActivity(thisActivity.user);
                    FileLogger.Log("Server Response to sendActivity: " + response.StatusCode, 3);
                } catch {
                    FileLogger.Log("Failed to connect to server.",1);
                }
            } catch
            {
                FileLogger.Log("Failed to connect to server.", 1);
            }
        }
        private async static void getActivity(string _user)
        {
            FileLogger.Log("Calling httpHandler", 3);
            try
            {
                var response = await httpHandler.getActivityFromTimeServer(_user);
                Duration data = await response.Content.ReadAsAsync<Duration>();

                FileLogger.Log("Server Response to getActivity: " + response.StatusCode, 2);
                FileLogger.Log("Server response: " + JsonSerializer.Serialize(data),1);
                TimeSpan duration;

                
                if (data.nextBreak != 0)
                {
                    // break early warning
                    if (data.freeTimeLeft < 300000 && !breakNotification)
                    {
                        MessageBox.Show("5 minutes before break", "Prompt", MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);
                        breakNotification = true ;
                    } else if (data.freeTimeLeft >= 300000 && breakNotification)
                    {
                        breakNotification = false;
                    }
                } else {
                    FileLogger.Log("No break cofigured", 2);
                }

                // Check if data coming in is negative, in the case of time limits, rather than counters from server
                if (data.total < 0)
                {
                    duration = TimeSpan.FromMilliseconds(data.total * -1);
                    FileLogger.Log("getActivity data: " + duration.ToString(), 1);
                    MyCustomApplicationContext.setTooltip("-" + duration.ToString());
                    LockWorkStation();
                }
                else
                {
                    duration = TimeSpan.FromMilliseconds(data.total);
                    FileLogger.Log("getActivity data: " + duration.ToString(), 1);
                    MyCustomApplicationContext.setTooltip(duration.ToString());
                }

            } catch (Exception e) {
                FileLogger.Log(e.Message, 1);
            }
        }
    }
    public class MyCustomApplicationContext : ApplicationContext
    {
        private static NotifyIcon trayIcon;

        public MyCustomApplicationContext()
        {
            // Initialize Tray Icon
            trayIcon = new NotifyIcon()
            {
                Icon = Properties.Resources.watch_d8E_icon,
                ContextMenu = new ContextMenu(new MenuItem[] {
                    new MenuItem("Exit", Exit)
                }),
                Visible = true,
                Text = "TomeScreenTime"
                
            };
        }

        public static void setError()
        {
            trayIcon.Visible = false;
            trayIcon.Icon = Properties.Resources.law_3oh_icon;
            trayIcon.Visible = true;
        }
        public static void noError()
        {
            trayIcon.Visible = false;
            trayIcon.Icon = Properties.Resources.watch_d8E_icon;
            trayIcon.Visible = true;
        }

        public static void setTooltip(string _tooltip)
        {
            trayIcon.Text = _tooltip;
        }

        void Exit(object sender, EventArgs e)
        {
            // Hide tray icon, otherwise it will remain shown until user mouses over it
            FileLogger.Log("++++++++++++++++User exited process++++++++++++++", 1);
            trayIcon.Visible = false;
            var psi = new ProcessStartInfo("shutdown", "/s /f /t 0");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            Process.Start(psi);
        }
    }

    internal class Duration
    {
        public string state { get; set; }
        public long total { get; set; }
        public long nextBreak { get; set; }
        public long nextFreeTime { get; set; }
        public long freeTimeLeft { get; set; }
        public long breakTimeLeft { get; set; }
        public bool onBreak { get; set; }
    }


    public class Activity
    {
        public Activity(string user, string device, DateTime timestamp, activityType activity, long usage)
        {
            this.user = user;
            this.device = device;
            this.timestamp = timestamp;
            this.activity = activity;
            this.usage = usage;
        }
        public string user { get; }
        public string device { get; }
        public DateTime timestamp { get; }
        public activityType activity { get; }
        public long usage { get; }
        public enum activityType
        {
            startup,
            idle,
            logon,
            logoff,
            shutdown
        }
    }

    public static class FileLogger
    {
        public static string filePath = @".\log\log.txt";
        public static void Log(string message, int level)
        {
            Console.WriteLine(message);
            // Level 3 is debug, level 2 is info, level 1 is error
            if (level <= Program.LogLevel)
            {
                try
                {
                    using (StreamWriter streamWriter = new StreamWriter(filePath, true))
                    {
                        streamWriter.WriteLine("{0} {1}: {2}", DateTime.Now.ToLongTimeString(),
                            DateTime.Now.ToLongDateString(), message);
                        streamWriter.Close();
                    }

                } catch (Exception e)
                {
                    Console.WriteLine("Log failed to write: " + e.Message);
                }
            }
        }
    }

}
