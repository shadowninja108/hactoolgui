﻿using HACGUI.Main.TitleManager.ApplicationWindow.Tabs.Extracts.Extractors;
using LibHac;
using LibHac.Fs.NcaUtils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace HACGUI.Main.TitleManager.ApplicationWindow.Tabs
{
    /// <summary>
    /// Interaction logic for ApplicationTitleTab.xaml
    /// </summary>
    public partial class ApplicationTitleTab : UserControl
    {
        private ApplicationElement Element => ApplicationWindow.Current.Element;

        public ApplicationTitleTab()
        {
            InitializeComponent();

            foreach(Title title in Element.OrderTitlesByBest())
            {
                TitleElement info = new TitleElement
                {
                    Title = title
                };
                ListView.Items.Add(info);
            }
        }

        private void ExtractClicked(object sender, RoutedEventArgs e)
        {
            List<SwitchFsNca> selected = new List<SwitchFsNca>();
            foreach (TitleElement info in ListView.Items)
                if (info.Selected)
                    foreach (NcaElement nca in info.Ncas)
                        selected.Add(nca.Nca);

            Window window = new ExtractPickerWindow(selected)
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();

            /*
            Dictionary<SectionType, List<NcaSection>> indexed = new Dictionary<SectionType, List<NcaSection>>();
            foreach (Title title in selected)
            {
                Nca nca = title.MainNca;
                if (nca.Header.ContentType != ContentType.Meta)
                    foreach (NcaSection section in nca.Sections)
                    {
                        if (section == null) continue;
                        if (!indexed.ContainsKey(section.Type)) indexed[section.Type] = new List<NcaSection>();
                        indexed[section.Type].Add(section);
                    }
                //string path = Path.Combine(folder, nca.Filename);
                //nca.GetStream().CopyTo(new FileStream(path, FileMode.Create));

            }
            ;*/
        }

        private void TitleDoubleClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (ListView.SelectedItem is TitleElement title)
            {
                Window window = new TitleInfoWindow(title)
                {
                    Owner = Window.GetWindow(this),
                    Title = $"Titles for {Element.Name}"
                };
                window.ShowDialog();
            }
        }

        private void MountClicked(object sender, RoutedEventArgs e)
        {
            List<Title> selected = new List<Title>();
            foreach (TitleElement info in ListView.Items)
                if (info.Selected)
                    selected.Add(info.Title);

            List<Title> orderedTitles = Element.OrderTitlesByBest();

            Title baseTitle = orderedTitles.FirstOrDefault(t => t.Metadata.Type == TitleType.Application);

            if (baseTitle == null && orderedTitles.Count == 1)
                baseTitle = orderedTitles.First();

            if (!IsMountable(baseTitle, selected))
            {
                MessageBox.Show("The base game isn't available, so the patch cannot be mounted.");
                return;
            }

            Dictionary<NcaFormatType, List<Tuple<SwitchFsNca, int>>> indexed = new Dictionary<NcaFormatType, List<Tuple<SwitchFsNca, int>>>();
            foreach (Title title in selected)
            {
                SwitchFsNca nca = title.MainNca;
                if (nca.Nca.Header.ContentType != ContentType.Meta)
                    for (int i = 0; i < 4; i++)
                    {
                        if (!nca.Nca.Header.IsSectionEnabled(i)) continue;
                        NcaFsHeader section = nca.Nca.Header.GetFsHeader(i);
                        if (!indexed.ContainsKey(section.FormatType)) indexed[section.FormatType] = new List<Tuple<SwitchFsNca, int>>();
                        indexed[section.FormatType].Add(new Tuple<SwitchFsNca, int>(nca, i));
                    }
            }
            Window window = new TitleMountDialog(indexed, baseTitle.MainNca)
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
        }

        private static bool IsMountable(Title main, List<Title> titles)
        {
            List<SwitchFsNca> updates = new List<SwitchFsNca>();
            List<SwitchFsNca> normal = new List<SwitchFsNca>();
            
            foreach (Title title in titles)
            {
                if (title.Metadata.Type == TitleType.Patch)
                {
                    if (main != null)
                        foreach (SwitchFsNca n in title.Ncas)
                            n.BaseNca = main.MainNca.Nca;
                    updates.Add(title.MainNca);
                }
                else if (title != main)
                    normal.Add(title.MainNca);
            }

            return !(updates.Any() && main == null);
        }

        private void CopyTitleIdClicked(object sender, RoutedEventArgs e)
        {
            MenuItem item = sender as MenuItem;
            ContextMenu contextMenu = item.Parent as ContextMenu;
            ListView listView = contextMenu.PlacementTarget as ListView;
            if (listView.SelectedItem is TitleElement element)
                Clipboard.SetText(string.Format("{0:x16}", element.TitleId));
        }
    }
}
