﻿using HACGUI.Extensions;
using HACGUI.Utilities;
using IniParser;
using IniParser.Model;
using libusbK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using static HACGUI.Utilities.Native;

namespace HACGUI.Services
{
    // Payload injecting based on TegraSharp (aka copied and pasted)
    // https://github.com/simontime/TegraSharp

    public static class InjectService
    {
        public static UsbDeviceInfo WMIDeviceInfo;
        public static UsbK Device;
        private static UsbKWrapper DeviceWrapper;

        private static readonly byte[] Intermezzo =
{
            0x44, 0x00, 0x9F, 0xE5, // LDR   R0, [PC, #0x44]
            0x01, 0x11, 0xA0, 0xE3, // MOV   R1, #0x40000000
            0x40, 0x20, 0x9F, 0xE5, // LDR   R2, [PC, #0x40]
            0x00, 0x20, 0x42, 0xE0, // SUB   R2, R2, R0
            0x08, 0x00, 0x00, 0xEB, // BL    #0x28
            0x01, 0x01, 0xA0, 0xE3, // MOV   R0, #0x40000000
            0x10, 0xFF, 0x2F, 0xE1, // BX    R0
            0x00, 0x00, 0xA0, 0xE1, // MOV   R0, R0
            0x2C, 0x00, 0x9F, 0xE5, // LDR   R0, [PC, #0x2C]
            0x2C, 0x10, 0x9F, 0xE5, // LDR   R1, [PC, #0x2C]
            0x02, 0x28, 0xA0, 0xE3, // MOV   R2, #0x20000
            0x01, 0x00, 0x00, 0xEB, // BL    #0xC
            0x20, 0x00, 0x9F, 0xE5, // LDR   R0, [PC, #0x20]
            0x10, 0xFF, 0x2F, 0xE1, // BX    R0
            0x04, 0x30, 0x90, 0xE4, // LDR   R3, [R0], #4
            0x04, 0x30, 0x81, 0xE4, // STR	 R3, [R1], #4
            0x04, 0x20, 0x52, 0xE2, // SUBS	 R2, R2, #4
            0xFB, 0xFF, 0xFF, 0x1A, // BNE	 #0xFFFFFFF4
            0x1E, 0xFF, 0x2F, 0xE1, // BX	 LR
            0x20, 0xF0, 0x01, 0x40, // ANDMI PC, R1, R0, LSR #32
            0x5C, 0xF0, 0x01, 0x40, // ANDMI PC, R1, IP, ASR R0
            0x00, 0x00, 0x02, 0x40, // ANDMI R0, R2, R0
            0x00, 0x00, 0x01, 0x40  // ANDMI R0, R1, R0
        };

        public static bool LibusbKInstalled => WMIDeviceInfo?.Service != null;

        private const string VID = "0955";
        private const string PID = "7321";

        private static readonly ManagementEventWatcher CreateWatcher, DeleteWatcher;

        private static bool Started = false;
        private static int Writes;
        private static readonly object WaitForReadyLock = new object();
        private static Task IniTask;
        public static bool WaitingForIniInject => IniTask != null;

        public static event Action DeviceInserted, DeviceRemoved, IniInjectFinished, ErrorOccurred;

        static InjectService()
        {
            // Create event handlers to detect when a device is added or removed
            CreateWatcher = new ManagementEventWatcher();
            WqlEventQuery createQuery = new WqlEventQuery("SELECT * FROM __InstanceCreationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_PnPEntity'");
            CreateWatcher.EventArrived += new EventArrivedEventHandler((s, e) =>
            {
                Refresh();
            });
            CreateWatcher.Query = createQuery;

            DeleteWatcher = new ManagementEventWatcher();
            WqlEventQuery deleteQuery = new WqlEventQuery("SELECT * FROM __InstanceDeletionEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_PnPEntity'");
            DeleteWatcher.EventArrived += new EventArrivedEventHandler((s, e) =>
            {
                WMIDeviceInfo = null;
                Refresh();
                if (WMIDeviceInfo == null)
                    DeviceRemoved?.Invoke();
            });
            DeleteWatcher.Query = deleteQuery;

            DeviceInserted += () =>
            {
                StatusService.RCMStatus = StatusService.Status.OK;
            };

            DeviceRemoved += () =>
            {
                StatusService.RCMStatus = StatusService.Status.Incorrect;
            };
        }

        public static void Start()
        {
            if (Started)
                throw new Exception("Inject service is already started!");

            if (HACGUIKeyset.TempLockpickPayloadFileInfo.Exists)
                HACGUIKeyset.TempLockpickPayloadFileInfo.Delete();

            Refresh();
            if (WMIDeviceInfo != null)
                Task.Run(() => DeviceInserted?.Invoke());

            CreateWatcher.Start();
            DeleteWatcher.Start();

            Started = true;
        }

        public static void Stop()
        {
            if (!Started)
                throw new Exception("NAND service hasn't started yet!");

            CreateWatcher.Stop();
            DeleteWatcher.Stop();

            Started = false;
        }

        private static byte[] SwizzlePayload(byte[] payload)
        {
            var buf = new byte[(int)Math.Ceiling((66216m + payload.Length) / 0x1000) * 0x1000];
            using (var mem = new MemoryStream())
            using (var wrt = new BinaryWriter(mem))
            {
                wrt.Write(0x30298);
                mem.Position = 0x2a8;
                for (var i = 0; i < 0x3c00; i++)
                    wrt.Write(0x4001f000);
                mem.Position = 0xf2a8;
                wrt.Write(Intermezzo);
                mem.Position = 0x102a8;
                wrt.Write(payload);
                Array.Copy(mem.ToArray(), buf, mem.Length);
                return buf;
            }
        }

        private static void WritePayload(UsbK wrt, byte[] payload)
        {
            var buffer = new byte[0x1000];

            for (var i = 0; i < payload.Length - 1; i += 0x1000, Writes++)
            {
                Buffer.BlockCopy(payload, i, buffer, 0, 0x1000);
                wrt.WritePipe(1, buffer, 0x1000, out _, IntPtr.Zero);
            }
        }

        public static void SendPayload(FileInfo info)
        {
            Writes = 0;
            byte[] payload = File.ReadAllBytes(info.FullName);
            var buf = new byte[0x10];
            Device.ReadPipe(0x81, buf, 0x10, out _, IntPtr.Zero);
            WritePayload(Device, SwizzlePayload(payload));

            if (Writes % 2 != 1)
            {
                Console.WriteLine("Switching buffers...");
                Device.WritePipe(1, new byte[0x1000], 0x1000, out _, IntPtr.Zero);
            }

            var setup = new WINUSB_SETUP_PACKET
            {
                RequestType = 0x81,
                Request = 0,
                Value = 0,
                Index = 0,
                Length = 0x7000
            };

            Task.Run(() => Device.ControlTransfer(setup, new byte[0x7000], 0x7000, out var b, IntPtr.Zero));
        }

        public static void SendIni(FileInfo info)
        {
            if (!WaitingForIniInject)
                IniTask = Task.Run(() =>
                {
                    bool attemptInject()
                    {
                        Device.AbortPipe(1);
                        Device.AbortPipe(0x81);

                        DirectoryInfo root = info.Directory;

                        MemloaderIniData iniData = new MemloaderIniData(info);

                        Task<bool> task;

                        foreach (LoadData currData in iniData.LoadData)
                        {
                            task = DeviceWrapper.WriteAsync("RECV".ToBytes(), 2000);
                            task.Wait();
                            if (!task.Result)
                                return false;

                            FileInfo file = root.GetFile(currData.SourceFile);
                            IEnumerable<byte> data = File.ReadAllBytes(file.FullName);
                            int skip = (int)currData.Skip;
                            int dataLength = data.Count() - skip;
                            if (currData.Count > 0)
                                dataLength = Math.Min(dataLength, (int)currData.Count);

                            IEnumerable<byte> address = currData.Dest.ToBytes(4).Reverse();
                            IEnumerable<byte> size = dataLength.ToBytes(4).Reverse();
                            byte[] bytesToSend = address.Concat(size).ToArray();

                            task = DeviceWrapper.WriteAsync(bytesToSend, 2000);
                            task.Wait();
                            if (!task.Result)
                                return false;

                            task = DeviceWrapper.WriteAsync(data.Skip(skip).Take(dataLength).ToArray(), 2000);
                            task.Wait();
                            if (!task.Result)
                                return false;
                        }

                        foreach (BootData currData in iniData.BootData)
                        {
                            Device.WritePipe(1, "BOOT".ToBytes(), 4, out int lengthTransfered, IntPtr.Zero);
                            if (lengthTransfered != 4)
                                return false;

                            IEnumerable<byte> pc = currData.PC.ToBytes(4).Reverse();
                            task = DeviceWrapper.WriteAsync(pc.ToArray(), 2000);
                            task.Wait();
                            if (!task.Result)
                                return false;
                        }
                        return true;
                    };

                    WaitForReady();
                    while (!attemptInject())
                        WaitForReady();
                })
                .ContinueWith(t => {
                    IniTask = null; // task is complete, discard
                    IniInjectFinished?.Invoke();
                })
                .ContinueWith(t => ErrorOccurred?.Invoke(), TaskContinuationOptions.OnlyOnFaulted); // catch exceptions and inform delegate
        }

        public struct LoadData
        {
            public string
                SourceFile;
            public ulong
                Skip,
                Count,
                Dest;
        }

        public struct BootData
        {
            public ulong PC;
        }

        class MemloaderIniData
        {
            public List<LoadData> LoadData = new List<LoadData>();
            public List<BootData> BootData = new List<BootData>();

            public MemloaderIniData(FileInfo info)
            {
                FileIniDataParser parser = new FileIniDataParser();
                IniData iniData = parser.ReadFile(info.FullName);

                foreach (SectionData entry in iniData.Sections)
                {
                    string sectionName = entry.SectionName.Substring(0, entry.SectionName.IndexOf(":"));
                    switch (sectionName)
                    {
                        case "load":
                            LoadData loadData = new LoadData();
                            foreach (KeyData key in entry.Keys)
                                switch (key.KeyName)
                                {
                                    case "if":
                                        loadData.SourceFile = key.Value;
                                        break;
                                    case "skip":
                                        loadData.Skip = Convert.ToUInt64(key.Value.Substring(2), 16);
                                        break;
                                    case "count":
                                        loadData.Count = Convert.ToUInt64(key.Value.Substring(2), 16);
                                        break;
                                    case "dst":
                                        loadData.Dest = Convert.ToUInt64(key.Value.Substring(2), 16);
                                        break;
                                }
                            LoadData.Add(loadData);
                            break;
                        case "boot":
                            BootData bootData = new BootData();
                            foreach (KeyData key in entry.Keys)
                                switch (key.KeyName)
                                {
                                    case "pc":
                                        bootData.PC = Convert.ToUInt64(key.Value.Substring(2), 16);
                                        break;
                                }
                            BootData.Add(bootData);
                            break;
                    }
                }
            }
        }

        class UsbKWrapper
        {
            private readonly UsbK Device;
            public UsbKWrapper(UsbK device)
            {
                Device = device;
            }

            public async Task<byte[]> ReadAsync(int timeout)
            {
                return await Task.Run(async () =>
                {

                    Task<byte[]> readTask = new Task<byte[]>(() => Read());
                    readTask.Start();

                    Task runTimeout = Task.Run(async () =>
                    {
                        await Task.Delay(timeout);

                        while (!readTask.IsCompleted)
                            Device.AbortPipe(0x81);

                    });

                    return await readTask;
                });
            }

            public byte[] Read()
            {
                byte[] buffer = new byte[0x1000];
                if (Device.ReadPipe(0x81, buffer, buffer.Length, out int length, IntPtr.Zero))
                {
                    byte[] data = new byte[length];
                    Array.Copy(buffer, data, length);
                    return data;
                }
                return null;
            }

            public bool Write(byte[] data)
            {
                bool result = Device.WritePipe(1, data, data.Length, out int bytesTransfered, IntPtr.Zero);
                if (!result)
                    return false;
                if (bytesTransfered != data.Length)
                    return false;
                return true;
            }

            public async Task<bool> WriteAsync(byte[] data, int timeout)
            {
                return await Task.Run(async () =>
                {

                    Task<bool> writeTask = new Task<bool>(() => Write(data));
                    writeTask.Start();

                    Task runTimeout = Task.Run(async () =>
                    {
                        await Task.Delay(timeout);

                        while (!writeTask.IsCompleted)
                            Device.AbortPipe(1);
                    });

                    return await writeTask;
                });
            }
        }
        public static void WaitForReady()
        {
            lock (WaitForReadyLock)
            {
                byte[] expected = "READY.\n".ToBytes();
                byte[] buffer = new byte[expected.Length];
                while (buffer == null || !buffer.SequenceEqual(expected))
                {
                    Task<byte[]> readTask = DeviceWrapper.ReadAsync(2000);
                    readTask.Wait();
                    buffer = readTask.Result;
                }
            }
        }

        public static void Refresh()
        {
            Scan();
            if (WMIDeviceInfo != null)
                DeviceInserted?.Invoke();
        }

        public static void Scan()
        {
            WMIDeviceInfo = null;
            Device = null;
            DeviceWrapper = null;
            foreach (UsbDeviceInfo info in AllUsbDevices)
                if (info.DeviceID.StartsWith($"USB\\VID_{VID}&PID_{PID}"))
                {
                    WMIDeviceInfo = info;

                    if (!LibusbKInstalled)
                    {
                        MessageBoxResult result = MessageBox.Show("You have plugged in your console, but it lacks the libusbK driver. Want to install it? (You cannot inject anything until this is done)", "", MessageBoxButton.YesNo);
                        if (result == MessageBoxResult.Yes)
                            InstallDriver();
                        WMIDeviceInfo = AllUsbDevices.First(x => x.DeviceID == WMIDeviceInfo.DeviceID); // we need to refresh the info
                    }

                    if (LibusbKInstalled)
                    {
                        var patternMatch = new KLST_PATTERN_MATCH { ClassGUID = WMIDeviceInfo.ClassGuid };
                        var deviceList = new LstK(0, ref patternMatch);
                        deviceList.MoveNext(out KLST_DEVINFO_HANDLE deviceInfo);

                        Device = new UsbK(deviceInfo);
                        Device.SetAltInterface(0, false, 0);
                        DeviceWrapper = new UsbKWrapper(Device);
                    }
                    break;
                }
        }

        public static void InstallDriver()
        {
            DirectoryInfo workingDirectory = HACGUIKeyset.ApxInstallerFolderInfo;
            FileInfo catSignerFile = workingDirectory.GetFile("dpscat.exe");
            LaunchProgram(
                catSignerFile.FullName,
                () => { },
                asAdmin: true,
                workingDirectory: workingDirectory.FullName,
                wait: true);

            if (!workingDirectory.GetFile("nx.cat").Exists)
            {
                MessageBox.Show("Failed to sign driver.");
                return;
            }

            string fileName = "dpinst";
            if (Environment.Is64BitOperatingSystem)
                fileName += "64";
            else
                fileName += "32";
            fileName += ".exe";

            LaunchProgram(
                workingDirectory.GetFile(fileName).FullName,
                () => { },
                asAdmin: true,
                wait: true);
        }
    }
}
