﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HACGUI.Extensions;
using HACGUI.Main.TitleManager;
using LibHac;
using LibHac.IO;
using LibHac.IO.Save;
using static HACGUI.Main.TitleManager.FSView;

namespace HACGUI.Services
{
    public class DeviceService
    {
        public delegate void TitlesChangedEvent(Dictionary<ulong, Application> apps, Dictionary<ulong, Title> titles, Dictionary<string, SaveDataFileSystem> saves);
        public static event TitlesChangedEvent TitlesChanged;

        public static Dictionary<ulong, Application> Applications;
        public static Dictionary<ulong, Title> Titles;
        public static Dictionary<string, SaveDataFileSystem> Saves;

        public static List<ApplicationElement> ApplicationElements => IndexTitles(Titles.Values);

        static FSView SDTitleView, NANDSystemTitleView, NANDUserTitleView;

        private static bool Started;

        public static void Start()
        {
            if (!Started)
            {
                Started = true;

                NANDSystemTitleView = new FSView(TitleSource.NAND);
                NANDUserTitleView = new FSView(TitleSource.NAND);
                SDTitleView = new FSView(TitleSource.SD);

                Applications = new Dictionary<ulong, Application>();
                Titles = new Dictionary<ulong, Title>();
                Saves = new Dictionary<string, SaveDataFileSystem>();

                SDService.OnSDPluggedIn += (drive) =>
                {
                    SDTitleView.Ready += (_, __) =>
                    {
                        StatusService.SDStatus = StatusService.Status.OK;
                        Update();
                    };
                    RootWindow.Current.Submit(new Task(() => SDTitleView.LoadFileSystem(() => SwitchFs.OpenSdCard(HACGUIKeyset.Keyset, new LocalFileSystem(drive.RootDirectory.FullName)))));


                    StatusService.SDStatus = StatusService.Status.Progress;
                };

                SDService.OnSDRemoved += (drive) =>
                {
                    StatusService.SDStatus = StatusService.Status.Incorrect;
                    SDTitleView.FS = null;
                    Update();
                };

                NANDService.OnNANDPluggedIn += () =>
                {
                    void onComplete()
                    {
                        StatusService.NANDStatus = StatusService.Status.OK;
                        Update();
                    };

                    int count = 0;
                    NANDSystemTitleView.Ready += (_, __) =>
                    {
                        count++;
                        if (count >= 2)
                            onComplete();
                    };
                    NANDUserTitleView.Ready += (_, __) =>
                    {
                        count++;
                        if (count >= 2)
                            onComplete();
                    };

                    RootWindow.Current.Submit(new Task(() =>
                    {
                        NANDSystemTitleView.LoadFileSystem(() => SwitchFs.OpenNandPartition(HACGUIKeyset.Keyset, NANDService.NAND.OpenSystemPartition()));
                        NANDUserTitleView.LoadFileSystem(() => SwitchFs.OpenNandPartition(HACGUIKeyset.Keyset, NANDService.NAND.OpenUserPartition()));
                    }));

                    StatusService.NANDStatus = StatusService.Status.Progress;
                };

                NANDService.OnNANDRemoved += () =>
                {
                    StatusService.NANDStatus = StatusService.Status.Incorrect;

                    NANDSystemTitleView.FS = null;
                    NANDUserTitleView.FS = null;

                    Update();
                };

                SDService.Start();
                NANDService.Start();
            }

            Update();
        }

        public static void Stop()
        {
            SDService.Stop();
            NANDService.Stop();

            Started = false;
        }

        public static void Update()
        {
            Dictionary<ulong, Application> totalApps = new Dictionary<ulong, Application>();
            Dictionary<ulong, Title> totalTitles = new Dictionary<ulong, Title>();
            Dictionary<string, SaveDataFileSystem> totalSaves = new Dictionary<string, SaveDataFileSystem>();

            if (SDTitleView.FS != null)
            {
                totalSaves.AddRange(SDTitleView.FS.Saves);
                lock (totalApps)
                {
                    foreach (KeyValuePair<ulong, LibHac.Application> kv in SDTitleView.FS.Applications)
                    {
                        ulong titleid = kv.Key;
                        Application app = kv.Value;
                        if (totalApps.ContainsKey(titleid))
                        {
                            totalApps[titleid].AddTitle(app.Main);
                            totalApps[titleid].AddTitle(app.Patch);
                            foreach (Title title in totalApps[titleid].AddOnContent)
                                totalApps[titleid].AddTitle(title);
                        }
                        else
                            totalApps[titleid] = app;
                    }
                    totalTitles.AddRange(SDTitleView.FS.Titles);
                }
            }
            if (NANDSystemTitleView.FS != null)
            {
                totalSaves.AddRange(NANDSystemTitleView.FS.Saves);
                totalApps.AddRange(NANDSystemTitleView.FS.Applications);
                totalTitles.AddRange(NANDSystemTitleView.FS.Titles);
            }
            if (NANDUserTitleView.FS != null)
            {
                totalSaves.AddRange(NANDUserTitleView.FS.Saves);
                lock (totalApps) // ensure threads don't try to modify list while iterating through it
                {
                    foreach (KeyValuePair<ulong, LibHac.Application> kv in NANDUserTitleView.FS.Applications)
                    {
                        ulong titleid = kv.Key;
                        LibHac.Application app = kv.Value;
                        if (totalApps.ContainsKey(titleid))
                        {
                            if (app.Main != null)
                                totalApps[titleid].AddTitle(app.Main);
                            if (app.Patch != null)
                                totalApps[titleid].AddTitle(app.Patch);
                            foreach (Title title in totalApps[titleid].AddOnContent)
                                if (title != null)
                                    totalApps[titleid].AddTitle(title);
                        }
                        else
                            totalApps[titleid] = app;
                    }
                    totalTitles.AddRange(NANDUserTitleView.FS.Titles);
                }
            }

            Applications.Clear();
            Titles.Clear();
            Saves.Clear();
            Applications.AddRange(totalApps);
            Titles.AddRange(totalTitles);
            Saves.AddRange(totalSaves);

            TitlesChanged(Applications, Titles, Saves);
        }


        public static List<ApplicationElement> IndexTitles(IEnumerable<Title> titles)
        {
            List<ApplicationElement> elements = new List<ApplicationElement>();

            foreach (Title title in titles)
            {
                if (title != null)
                {
                    ApplicationElement searchedApp = elements.FirstOrDefault(x => x?.BaseTitleId == title.GetBaseTitleID());
                    if (searchedApp != null)
                        searchedApp.Titles.Add(title);
                    else
                    {
                        ApplicationElement app = new ApplicationElement();
                        app.Titles.Add(title);
                        elements.Add(app);
                    }
                }
            }

            return elements;
        }
    }

}