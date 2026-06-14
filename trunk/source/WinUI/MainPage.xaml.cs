using System;
using System.Collections.Generic;
using System.CommandLine.Parsing;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.AppLifecycle;
using SunJWBase;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FilesHashWUI
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private enum MainPageControlStat
        {
            MainPageNone = 0, // clear stat
            MainPageCalcIng,  // calculating
            MainPageCalcFinish, // calculating finished/stopped
            MainPageVerify, // verifying
            MainPageWaitingExit, // waiting thread stop and exit
        };

        // Which hash types to display (and export). The engine always computes
        // all four in a single file pass; this is a display/export filter only.
        [Flags]
        private enum DisplayHashType
        {
            None = 0,
            MD5 = 1 << 0,
            SHA1 = 1 << 1,
            SHA256 = 1 << 2,
            SHA512 = 1 << 3,
            All = MD5 | SHA1 | SHA256 | SHA512,
        };

        private static readonly (string Label, DisplayHashType Type)[] s_hashMenuEntries =
        [
            ("MD5", DisplayHashType.MD5),
            ("SHA1", DisplayHashType.SHA1),
            ("SHA256", DisplayHashType.SHA256),
            ("SHA512", DisplayHashType.SHA512),
        ];

        private const string KeyUppercase = "Uppercase";
        private const string KeyDisplayHashTypes = "DisplayHashTypes";
        private const string ArgPaths = "-paths";

        private MainWindow m_mainWindow = null;
        private ResourceLoader m_resourceLoaderMain = WinUIHelper.GetCurrentResourceLoader();

        private bool m_pageInited = false;
        private bool m_pageLoaded = false;
        private bool m_pendingScrollToBottom = false;

        private ContentDialog m_dialogFind = null;
        private TextBox m_textBoxFindHash = null;

        private Paragraph m_paragraphMain = null;
        private Paragraph m_paragraphResult = null;
        private Paragraph m_paragraphFind = null;
        private List<Hyperlink> m_hyperlinksMain = null;
        private List<Hyperlink> m_hyperlinksResult = [];
        private List<Hyperlink> m_hyperlinksFind = [];
        private MenuFlyout m_menuFlyoutTextMain = null;
        private Hyperlink m_hyperlinkClicked = null;
        private Run m_runPrepare = null;

        private MainPageControlStat m_mainPageStat;

        private bool m_uppercaseChecked = false;

        private DisplayHashType m_displayHashTypes = DisplayHashType.All;
        // Terminal per-file results (ResultAll / ResultError), kept for display
        // rebuild on hash-type change and for export.
        private readonly List<ResultDataNet> m_resultDataList = [];

        private int m_inMainQueue = 0;
        private int m_outMainQueue = 0;
        private const int m_maxDiffQueue = 3;
        List<Inline> m_inlinesQueue = [];

        private long m_calcStartTime = 0;
        private long m_calcEndTime = 0;

        public MainPage()
        {
            InitializeComponent();

            m_mainWindow = MainWindow.CurrentWindow;

            m_mainWindow.UIBridgeHandlers.PreparingCalcHandler += UIBridgeHandlers_PreparingCalcHandler;
            m_mainWindow.UIBridgeHandlers.RemovePreparingCalcHandler += UIBridgeHandlers_RemovePreparingCalcHandler;
            m_mainWindow.UIBridgeHandlers.CalcStopHandler += UIBridgeHandlers_CalcStopHandler;
            m_mainWindow.UIBridgeHandlers.CalcFinishHandler += UIBridgeHandlers_CalcFinishHandler;
            m_mainWindow.UIBridgeHandlers.ShowFileNameHandler += UIBridgeHandlers_ShowFileNameHandler;
            m_mainWindow.UIBridgeHandlers.ShowFileMetaHandler += UIBridgeHandlers_ShowFileMetaHandler;
            m_mainWindow.UIBridgeHandlers.ShowFileHashHandler += UIBridgeHandlers_ShowFileHashHandler;
            m_mainWindow.UIBridgeHandlers.ShowFileErrHandler += UIBridgeHandlers_ShowFileErrHandler;
            m_mainWindow.UIBridgeHandlers.UpdateProgWholeHandler += UIBridgeHandlers_UpdateProgWholeHandler;

            m_mainWindow.IsAbleToCalc = IsAbleToCalcFiles;
            m_mainWindow.IsCalculating = IsCalculating;

            m_mainWindow.RedirectedEventHandler += OnRedirected;
            m_mainWindow.OnCloseStopEventHandler += () => StopHashCalc(true);
            m_mainWindow.OnDropFilesEventHandler += StartHashCalc;

            InitLayout();
        }

        private void InitLayout()
        {
            InitDialogFind();
            InitMenuFlyoutTextMain();
        }

        private void InitDialogFind()
        {
            m_textBoxFindHash = new()
            {
                Height = (double)Application.Current.Resources["TextControlThemeMinHeight"],
                Width = 400,
                PlaceholderText = m_resourceLoaderMain.GetString("HashValue")
            };
            m_textBoxFindHash.TextChanged += TextBoxFindHash_TextChanged;

            m_dialogFind = new()
            {
                XamlRoot = m_mainWindow.Content.XamlRoot,
                Title = m_resourceLoaderMain.GetString("FindDialogTitle"),
                // MaxWidth = ActualWidth,
                PrimaryButtonText = "OK",
                SecondaryButtonText = "Cancel",
                Content = m_textBoxFindHash,
                DefaultButton = ContentDialogButton.Primary
            };
        }

        private void InitMenuFlyoutTextMain()
        {
            m_menuFlyoutTextMain = new()
            {
                XamlRoot = m_mainWindow.Content.XamlRoot
            };

            MenuFlyoutItem menuItemCopy = new();
            menuItemCopy.Text = m_resourceLoaderMain.GetString("MenuItemCopy");
            menuItemCopy.Click += MenuItemCopy_Click;
            MenuFlyoutItem menuItemGoogle = new();
            menuItemGoogle.Text = m_resourceLoaderMain.GetString("MenuItemGoogle");
            menuItemGoogle.Click += MenuItemGoogle_Click;
            MenuFlyoutItem menuItemVirusTotal = new();
            menuItemVirusTotal.Text = m_resourceLoaderMain.GetString("MenuItemVirusTotal");
            menuItemVirusTotal.Click += MenuItemVirusTotal_Click;

            m_menuFlyoutTextMain.Items.Add(menuItemCopy);
            m_menuFlyoutTextMain.Items.Add(new MenuFlyoutSeparator());
            m_menuFlyoutTextMain.Items.Add(menuItemGoogle);
            m_menuFlyoutTextMain.Items.Add(menuItemVirusTotal);
        }

        private void ShowAboutPage()
        {
            Frame.Navigate(typeof(AboutPage));
        }

        private void CloseAboutPage()
        {
            if (Frame.CanGoBack)
                Frame.GoBack();
        }

        private void BringWindowToFront()
        {
            DispatcherQueue.TryEnqueue(() => Win32Helper.SetForegroundWindow(m_mainWindow.HWNDHandle));
        }

        private void ScrollTextMainToBottom()
        {
            if (m_pageLoaded)
                WinUIHelper.ScrollViewerToBottom(ScrollViewerMain);
            else
                m_pendingScrollToBottom = true;
        }

        private Paragraph CreateParagraphForTextMain()
        {
            Paragraph paragraph = new()
            {
                FontFamily = new("Consolas"),
                LineHeight = 18,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight
            };
            return paragraph;
        }

        private Hyperlink GenHyperlinkFromStringForRichTextMain(string strContent)
        {
            return WinUIHelper.GenHyperlinkFromString(strContent, RichTextMainHyperlink_Click);
        }

        private void AppendInlinesToTextMain(List<Inline> inlines, bool scrollBottom = true)
        {
            if (inlines != null)
            {
                foreach (Inline inline in inlines)
                {
                    m_paragraphMain.Inlines.Add(inline);
                }
            }
            if (scrollBottom)
            {
                ScrollTextMainToBottom();
            }
        }

        private void AppendInlineToTextMain(Inline inline)
        {
            List<Inline> inlines = [inline];
            AppendInlinesToTextMain(inlines);
        }

        private bool CanUpdateTextMain()
        {
            if (!IsCalculating())
            {
                return true;
            }

            if (m_inMainQueue < 100)
            {
                return (m_inMainQueue - m_outMainQueue < m_maxDiffQueue);
            }
            else
            {
                return (m_inlinesQueue.Count > (m_inMainQueue / 2));
            }
        }

        private void AppendInlinesQueueToTextMain()
        {
            if (m_inlinesQueue.Count > 0)
            {
                AppendInlinesToTextMain(m_inlinesQueue);
                m_inlinesQueue.Clear();
            }
        }

        private void ClearTextMain()
        {
            m_paragraphMain.Inlines.Clear();
        }

        private void SetPageControlStat(MainPageControlStat newStat)
        {
            switch (newStat)
            {
                case MainPageControlStat.MainPageNone:
                case MainPageControlStat.MainPageCalcFinish:
                    // MainPageControlStat.MainPageNone
                    if (newStat == MainPageControlStat.MainPageNone)
                    {
                        m_hyperlinksMain.Clear();
                        m_resultDataList.Clear();
                        m_mainWindow.HashMgmt.Clear();

                        ProgressBarMain.Value = 0;
                        m_mainWindow.SetTaskbarProgress(0);

                        TextBlockSpeed.Text = "";

                        Span spanInit = new();
                        string strPageInit = m_resourceLoaderMain.GetString("MainPageInitInfo");
                        spanInit.Inlines.Add(WinUIHelper.GenRunFromString(strPageInit));
                        spanInit.Inlines.Add(WinUIHelper.GenRunFromString("\r\n"));
                        ClearTextMain();
                        AppendInlineToTextMain(spanInit);
                    }
                    // Passthrough to MainPageControlStat.MainPageCalcFinish
                    m_calcEndTime = WinUIHelper.GetCurrentMilliSec();

                    ButtonOpen.Content = m_resourceLoaderMain.GetString("ButtonOpenOpen");
                    ButtonClear.IsEnabled = true;
                    ButtonVerify.IsEnabled = true;
                    CheckBoxUppercase.IsEnabled = true;
                    DropDownButtonHash.IsEnabled = true;
                    break;
                case MainPageControlStat.MainPageCalcIng:
                    CloseAboutPage();

                    m_calcStartTime = WinUIHelper.GetCurrentMilliSec();
                    m_mainWindow.HashMgmt.SetStop(false);

                    TextBlockSpeed.Text = "";
                    ButtonOpen.Content = m_resourceLoaderMain.GetString("ButtonOpenStop");
                    ButtonClear.IsEnabled = false;
                    ButtonVerify.IsEnabled = false;
                    CheckBoxUppercase.IsEnabled = false;
                    DropDownButtonHash.IsEnabled = false;

                    BringWindowToFront();
                    break;
                case MainPageControlStat.MainPageVerify:
                    ButtonVerify.IsEnabled = false;
                    break;
            }

            MainPageControlStat oldStat = m_mainPageStat;
            m_mainPageStat = newStat;

            if (oldStat == MainPageControlStat.MainPageWaitingExit &&
                m_mainPageStat == MainPageControlStat.MainPageCalcFinish)
            {
                // Wait to close
                DispatcherQueue.TryEnqueue(m_mainWindow.Close);
            }
        }

        private void UpdateUppercaseStat(bool saveLocalSetting = true)
        {
            bool? uppercaseIsChecked = CheckBoxUppercase.IsChecked;
            if (uppercaseIsChecked.HasValue && uppercaseIsChecked.Value)
            {
                m_uppercaseChecked = true;
            }
            else
            {
                m_uppercaseChecked = false;
            }
            if (saveLocalSetting)
            {
                WinUIHelper.SaveLocalSettings(KeyUppercase, m_uppercaseChecked);
            }
        }

        private void UpdateResultUppercase()
        {
            // Refresh stat
            UpdateUppercaseStat();

            // Refresh result & find
            List<List<Hyperlink>> hyperlinkLists = [];
            hyperlinkLists.Add(m_hyperlinksResult);
            hyperlinkLists.Add(m_hyperlinksFind);
            foreach (List<Hyperlink> hyperlinkListItr in hyperlinkLists)
            {
                foreach (Hyperlink hyperlink in hyperlinkListItr)
                {
                    if (hyperlink.Inlines.Count == 0)
                        continue;

                    string hyperLinkText = WinUIHelper.GetTextFromHyperlink(hyperlink);
                    if (m_uppercaseChecked)
                        hyperLinkText = hyperLinkText.ToUpper();
                    else
                        hyperLinkText = hyperLinkText.ToLower();

                    Run runInHyperlink = (Run)hyperlink.Inlines[0];
                    runInHyperlink.Text = hyperLinkText;
                }
            }
        }

        private void LoadDisplayHashTypes()
        {
            object objTypes = WinUIHelper.LoadLocalSettings(KeyDisplayHashTypes);
            if (objTypes is int intTypes)
            {
                m_displayHashTypes = (DisplayHashType)intTypes & DisplayHashType.All;
            }
            if (m_displayHashTypes == DisplayHashType.None)
            {
                m_displayHashTypes = DisplayHashType.All; // never leave nothing to show
            }
        }

        private void InitHashMenuFlyout()
        {
            DropDownButtonHash.Content = m_resourceLoaderMain.GetString("ButtonHashMenu");

            MenuFlyout menuFlyout = new();
            foreach ((string label, DisplayHashType type) in s_hashMenuEntries)
            {
                ToggleMenuFlyoutItem item = new()
                {
                    Text = label,
                    Tag = (int)type,
                    IsChecked = m_displayHashTypes.HasFlag(type),
                };
                item.Click += HashTypeMenuItem_Click;
                menuFlyout.Items.Add(item);
            }

            menuFlyout.Items.Add(new MenuFlyoutSeparator());

            MenuFlyoutItem exportItem = new()
            {
                Text = m_resourceLoaderMain.GetString("MenuExport"),
            };
            exportItem.Click += MenuExport_Click;
            menuFlyout.Items.Add(exportItem);

            DropDownButtonHash.Flyout = menuFlyout;
        }

        private void HashTypeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleMenuFlyoutItem item)
            {
                return;
            }

            // ToggleMenuFlyoutItem flips IsChecked before Click fires.
            DisplayHashType type = (DisplayHashType)(int)item.Tag;
            DisplayHashType newTypes = m_displayHashTypes;
            if (item.IsChecked)
            {
                newTypes |= type;
            }
            else
            {
                newTypes &= ~type;
            }

            // Keep at least one hash type visible.
            if (newTypes == DisplayHashType.None)
            {
                item.IsChecked = true;
                return;
            }

            m_displayHashTypes = newTypes;
            WinUIHelper.SaveLocalSettings(KeyDisplayHashTypes, (int)m_displayHashTypes);

            // Re-render already calculated results with the new selection.
            if (m_mainPageStat == MainPageControlStat.MainPageCalcFinish)
            {
                RefreshResultText();
            }
        }

        private void RefreshResultText()
        {
            // Only rebuild the main result view (not the find/verify view).
            if (m_mainPageStat == MainPageControlStat.MainPageVerify)
            {
                return;
            }

            UpdateUppercaseStat();

            // Reset streaming queue/counters before rebuilding.
            m_inlinesQueue.Clear();
            m_inMainQueue = 0;
            m_outMainQueue = 0;

            // Rebuild result paragraph from the stored results.
            m_paragraphMain = m_paragraphResult;
            m_hyperlinksMain = m_hyperlinksResult;
            m_paragraphResult.Inlines.Clear();
            m_hyperlinksResult.Clear();

            foreach (ResultDataNet resultData in m_resultDataList)
            {
                AppendFileResultToTextMain(resultData, m_uppercaseChecked);
            }
            AppendInlinesQueueToTextMain();

            m_inMainQueue = 0;
            m_outMainQueue = 0;
        }

        private bool HasExportableResults()
        {
            return m_resultDataList.Count > 0;
        }

        private string BuildResultsPlainText()
        {
            UpdateUppercaseStat();
            bool uppercase = m_uppercaseChecked;

            const string nl = "\r\n";
            System.Text.StringBuilder sb = new();
            foreach (ResultDataNet r in m_resultDataList)
            {
                if (r.EnumState == ResultStateNet.ResultNone)
                {
                    continue;
                }

                // File name.
                sb.Append(m_resourceLoaderMain.GetString("ResultFileName"));
                sb.Append(' ');
                sb.Append(r.Path);
                sb.Append(nl);

                // Meta (size + modified date).
                if (r.EnumState == ResultStateNet.ResultAll || r.EnumState == ResultStateNet.ResultMeta)
                {
                    string strShortSize = WinUIHelper.ConvertSizeToShortSizeStr(r.Size);
                    sb.Append(m_resourceLoaderMain.GetString("ResultFileSize"));
                    sb.Append(' ');
                    sb.Append(r.Size);
                    sb.Append(' ');
                    sb.Append(m_resourceLoaderMain.GetString("ResultByte"));
                    if (!string.IsNullOrEmpty(strShortSize))
                    {
                        sb.Append(" (");
                        sb.Append(strShortSize);
                        sb.Append(')');
                    }
                    sb.Append(nl);
                    sb.Append(m_resourceLoaderMain.GetString("ResultModifiedTime"));
                    sb.Append(' ');
                    sb.Append(r.ModifiedDate);
                    sb.Append(nl);
                }

                // Hashes (respect the selected hash types and uppercase setting).
                if (r.EnumState == ResultStateNet.ResultAll)
                {
                    foreach ((string label, DisplayHashType type) in s_hashMenuEntries)
                    {
                        if (!m_displayHashTypes.HasFlag(type))
                        {
                            continue;
                        }
                        string value = HashValueForType(r, type);
                        value = uppercase ? value.ToUpper() : value.ToLower();
                        sb.Append(label);
                        sb.Append(": ");
                        sb.Append(value);
                        sb.Append(nl);
                    }
                }

                // Error.
                if (r.EnumState == ResultStateNet.ResultError)
                {
                    sb.Append(r.Error);
                    sb.Append(nl);
                }

                sb.Append(nl);
            }

            return sb.ToString();
        }

        private async void MenuExport_Click(object sender, RoutedEventArgs e)
        {
            if (!HasExportableResults())
            {
                return;
            }

            string text = BuildResultsPlainText();

            FileSavePicker picker = new();
            InitializeWithWindow.Initialize(picker, m_mainWindow.HWNDHandle);
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("Text", new List<string>() { ".txt" });
            picker.SuggestedFileName = "fHash-export";

            StorageFile file = await picker.PickSaveFileAsync();
            if (file == null)
            {
                return;
            }

            await FileIO.WriteTextAsync(file, text);
        }

        private bool IsAbleToCalcFiles()
        {
            return !IsCalculating();
        }

        private bool IsCalculating()
        {
            return (m_mainPageStat == MainPageControlStat.MainPageCalcIng ||
                m_mainPageStat == MainPageControlStat.MainPageWaitingExit);
        }

        private void StartHashCalc(List<string> filePaths)
        {
            if (!IsAbleToCalcFiles())
            {
                return;
            }

            // ClearFindResult first
            if (m_mainPageStat == MainPageControlStat.MainPageVerify)
            {
                ClearFindResult();
            }
            // Stat can be MainPageNone after ClearFindResult
            if (m_mainPageStat == MainPageControlStat.MainPageNone)
            {
                ClearTextMain();
            }

            m_mainWindow.HashMgmt.AddFiles(filePaths.ToArray());

            UpdateUppercaseStat();
            m_mainWindow.HashMgmt.SetUppercase(m_uppercaseChecked);

            ProgressBarMain.Value = 0;
            m_mainWindow.SetTaskbarProgress(1);

            SetPageControlStat(MainPageControlStat.MainPageCalcIng);

            // Ready to go
            m_inMainQueue = 0;
            m_outMainQueue = 0;
            m_inlinesQueue.Clear();
            m_mainWindow.HashMgmt.StartHashThread();
        }

        private void StopHashCalc(bool needExit)
        {
            if (m_mainPageStat == MainPageControlStat.MainPageCalcIng)
            {
                m_mainWindow.HashMgmt.SetStop(true);

                if (needExit)
                {
                    SetPageControlStat(MainPageControlStat.MainPageWaitingExit);
                }
            }
        }

        private void CalculateFinished()
        {
            AppendInlinesQueueToTextMain();

            SetPageControlStat(MainPageControlStat.MainPageCalcFinish);

            int progMax = m_mainWindow.UIBridgeHandlers.GetProgMax();
            ProgressBarMain.Value = progMax;
            m_mainWindow.SetTaskbarProgress((ulong)progMax);

            long calcDurationTime = m_calcEndTime - m_calcStartTime;
            if (calcDurationTime > 10)
            {
                // speed is Bytes/ms
                double calcSpeed = ((double)m_mainWindow.HashMgmt.GetTotalSize()) / calcDurationTime;
                calcSpeed = calcSpeed * 1000; // Bytes/s
                ulong ulCalcSpeed = (ulong)calcSpeed;
                string strSpeed = "";
                if (ulCalcSpeed > 0)
                {
                    strSpeed = WinUIHelper.ConvertSizeToShortSizeStr(ulCalcSpeed, true);
                    if (!string.IsNullOrEmpty(strSpeed))
                    {
                        strSpeed += "/s";
                    }
                }
                TextBlockSpeed.Text = strSpeed;
            }
            else
            {
                TextBlockSpeed.Text = "";
            }
        }

        private void CalculateStopped()
        {
            AppendInlinesQueueToTextMain();
            AppendInlineToTextMain(WinUIHelper.GenRunFromString("\r\n"));

            SetPageControlStat(MainPageControlStat.MainPageCalcFinish);
            ProgressBarMain.Value = 0;
            m_mainWindow.SetTaskbarProgress(0);
        }

        private void AppendFileNameToTextMain(ResultDataNet resultData)
        {
            m_outMainQueue += 1;
            string strAppend = m_resourceLoaderMain.GetString("ResultFileName");
            strAppend += " ";
            strAppend += resultData.Path;
            m_inlinesQueue.Add(WinUIHelper.GenRunFromString(strAppend));
            m_inlinesQueue.Add(WinUIHelper.GenRunFromString("\r\n"));

            if (CanUpdateTextMain())
            {
                AppendInlinesQueueToTextMain();
            }
        }

        private void AppendFileMetaToTextMain(ResultDataNet resultData)
        {
            m_outMainQueue += 1;
            string strShortSize = WinUIHelper.ConvertSizeToShortSizeStr(resultData.Size);
            string strSize = m_resourceLoaderMain.GetString("ResultFileSize");
            strSize += " ";
            strSize += resultData.Size;
            strSize += " ";
            strSize += m_resourceLoaderMain.GetString("ResultByte");
            if (!string.IsNullOrEmpty(strShortSize))
            {
                strSize += " (";
                strSize += strShortSize;
                strSize += ")";
            }
            string strModifiedTime = m_resourceLoaderMain.GetString("ResultModifiedTime");
            strModifiedTime += " ";
            strModifiedTime += resultData.ModifiedDate;
            m_inlinesQueue.Add(WinUIHelper.GenRunFromString(strSize));
            m_inlinesQueue.Add(WinUIHelper.GenRunFromString("\r\n"));
            m_inlinesQueue.Add(WinUIHelper.GenRunFromString(strModifiedTime));
            m_inlinesQueue.Add(WinUIHelper.GenRunFromString("\r\n"));
            if (!string.IsNullOrEmpty(resultData.Version))
            {
                string strVersion = m_resourceLoaderMain.GetString("ResultFileVersion");
                strVersion += " ";
                strVersion += resultData.Version;
                m_inlinesQueue.Add(WinUIHelper.GenRunFromString(strVersion));
                m_inlinesQueue.Add(WinUIHelper.GenRunFromString("\r\n"));
            }

            if (CanUpdateTextMain())
            {
                AppendInlinesQueueToTextMain();
            }
        }

        private static string HashValueForType(ResultDataNet resultData, DisplayHashType type)
        {
            return type switch
            {
                DisplayHashType.MD5 => resultData.MD5,
                DisplayHashType.SHA1 => resultData.SHA1,
                DisplayHashType.SHA256 => resultData.SHA256,
                DisplayHashType.SHA512 => resultData.SHA512,
                _ => "",
            };
        }

        private void AppendFileHashToTextMain(ResultDataNet resultData, bool uppercase)
        {
            m_outMainQueue += 1;

            // Only show the hash types the user selected in the Hash menu.
            foreach ((string label, DisplayHashType type) in s_hashMenuEntries)
            {
                if (!m_displayHashTypes.HasFlag(type))
                {
                    continue;
                }

                string value = HashValueForType(resultData, type);
                value = uppercase ? value.ToUpper() : value.ToLower();

                m_inlinesQueue.Add(WinUIHelper.GenRunFromString(label + ": "));
                Hyperlink hyperlink = GenHyperlinkFromStringForRichTextMain(value);
                m_hyperlinksMain.Add(hyperlink);
                m_inlinesQueue.Add(hyperlink);
                m_inlinesQueue.Add(WinUIHelper.GenRunFromString("\r\n"));
            }

            m_inlinesQueue.Add(WinUIHelper.GenRunFromString("\r\n"));

            if (CanUpdateTextMain())
            {
                AppendInlinesQueueToTextMain();
            }
        }

        private void AppendFileErrToTextMain(ResultDataNet resultData)
        {
            m_outMainQueue += 1;
            string strAppend = resultData.Error;
            m_inlinesQueue.Add(WinUIHelper.GenRunFromString(strAppend));
            m_inlinesQueue.Add(WinUIHelper.GenRunFromString("\r\n\r\n"));

            if (CanUpdateTextMain())
            {
                AppendInlinesQueueToTextMain();
            }
        }

        private void AppendFileResultToTextMain(ResultDataNet resultData, bool uppercase)
        {
            if (resultData.EnumState == ResultStateNet.ResultNone)
            {
                return;
            }

            if (resultData.EnumState == ResultStateNet.ResultAll ||
                resultData.EnumState == ResultStateNet.ResultMeta ||
                resultData.EnumState == ResultStateNet.ResultError ||
                resultData.EnumState == ResultStateNet.ResultPath)
            {
                AppendFileNameToTextMain(resultData);
            }

            if (resultData.EnumState == ResultStateNet.ResultAll ||
                resultData.EnumState == ResultStateNet.ResultMeta)
            {
                AppendFileMetaToTextMain(resultData);
            }

            if (resultData.EnumState == ResultStateNet.ResultAll)
            {
                AppendFileHashToTextMain(resultData, uppercase);
            }

            if (resultData.EnumState == ResultStateNet.ResultError)
            {
                AppendFileErrToTextMain(resultData);
            }

            if (resultData.EnumState != ResultStateNet.ResultAll &&
                resultData.EnumState != ResultStateNet.ResultError)
            {
                AppendInlineToTextMain(WinUIHelper.GenRunFromString("\r\n"));
            }
        }

        private async void ShowFindDialog()
        {
            m_textBoxFindHash.Text = "";
            ContentDialogResult result = await m_dialogFind.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                string strHashToFind = m_textBoxFindHash.Text;
                ResultDataNet[] resultDataNetArray = m_mainWindow.HashMgmt.FindResult(strHashToFind);
                DispatcherQueue.TryEnqueue(() => ShowFindResult(strHashToFind, resultDataNetArray));
            }
        }

        private void ShowFindResult(string strHashToFind, ResultDataNet[] resultDataNetArray)
        {
            // Fix strange behavior
            ScrollViewerMain.ChangeView(null, 0.0, null, true);
            ScrollViewerMain.ChangeView(0.01, null, null); // WTF?
            ScrollViewerMain.IsEnabled = false;

            // Switch m_paragraphMain
            RichTextMain.Blocks.Clear();
            m_paragraphMain = m_paragraphFind;
            RichTextMain.Blocks.Add(m_paragraphMain);
            m_hyperlinksMain = m_hyperlinksFind;

            // Show result
            List<Inline> inlines = [];
            string strFindResult = m_resourceLoaderMain.GetString("FindResultTitle");
            inlines.Add(WinUIHelper.GenRunFromString(strFindResult));
            inlines.Add(WinUIHelper.GenRunFromString("\r\n"));
            string strHashValue = m_resourceLoaderMain.GetString("HashValue");
            strHashValue += ": ";
            inlines.Add(WinUIHelper.GenRunFromString(strHashValue));
            inlines.Add(WinUIHelper.GenRunFromString(strHashToFind));
            inlines.Add(WinUIHelper.GenRunFromString("\r\n"));
            string strFindResultBegin = m_resourceLoaderMain.GetString("FindResultBegin");
            inlines.Add(WinUIHelper.GenRunFromString(strFindResultBegin));
            inlines.Add(WinUIHelper.GenRunFromString("\r\n\r\n"));
            AppendInlinesToTextMain(inlines);

            if (resultDataNetArray == null || resultDataNetArray.Length == 0)
            {
                // No match
                List<Inline> inlinesResult = [];
                string strFindNoResult = m_resourceLoaderMain.GetString("FindNoResult");
                inlinesResult.Add(WinUIHelper.GenRunFromString(strFindNoResult));
                inlinesResult.Add(WinUIHelper.GenRunFromString("\r\n"));
                AppendInlinesToTextMain(inlinesResult);
            }
            else
            {
                // Found some
                foreach (ResultDataNet resultData in resultDataNetArray)
                {
                    AppendFileResultToTextMain(resultData, m_uppercaseChecked);
                }
            }

            SetPageControlStat(MainPageControlStat.MainPageVerify);

            // Fix strange behavior
            ScrollViewerMain.ChangeView(0.0, null, null);
            ScrollViewerMain.IsEnabled = true;
            ScrollTextMainToBottom();
        }

        private void ClearFindResult()
        {
            // Switch m_paragraphMain
            RichTextMain.Blocks.Clear();
            m_paragraphMain = m_paragraphResult;
            RichTextMain.Blocks.Add(m_paragraphMain);
            ScrollTextMainToBottom();
            m_hyperlinksMain = m_hyperlinksResult;

            if (m_mainWindow.HashMgmt.GetResultCount() > 0)
            {
                SetPageControlStat(MainPageControlStat.MainPageCalcFinish);
            }
            else
            {
                SetPageControlStat(MainPageControlStat.MainPageNone);
            }

            // Clear find result
            m_paragraphFind.Inlines.Clear();
            m_hyperlinksFind.Clear();
        }

        private void ShowRichTextMainMenuFlyout()
        {
            double scale = m_mainWindow.Scale;
            int menuOffsetX = 4;
            int menuOffsetY = 2;
            System.Drawing.Point sdPointCursor = m_mainWindow.GetCursorRelativePoint();
            Windows.Foundation.Point wfPointCuror = new((sdPointCursor.X / scale) + menuOffsetX, (sdPointCursor.Y / scale) + menuOffsetY);
            m_menuFlyoutTextMain?.ShowAt(null, wfPointCuror);
        }

        private void MenuItemCopy_Click(object sender, RoutedEventArgs e)
        {
            if (m_hyperlinkClicked == null)
                return;

            string strHash = WinUIHelper.GetTextFromHyperlink(m_hyperlinkClicked);
            NativeHelper nativeHelper = new();
            nativeHelper.SetClipboardText(strHash);
        }

        private void MenuItemGoogle_Click(object sender, RoutedEventArgs e)
        {
            if (m_hyperlinkClicked == null)
                return;

            string strHash = WinUIHelper.GetTextFromHyperlink(m_hyperlinkClicked);
            string strUrl = string.Format("https://www.google.com/search?q={0}&ie=utf-8&oe=utf-8", strHash);
            WinUIHelper.OpenUrl(strUrl);
        }

        private void MenuItemVirusTotal_Click(object sender, RoutedEventArgs e)
        {
            if (m_hyperlinkClicked == null)
                return;

            string strHash = WinUIHelper.GetTextFromHyperlink(m_hyperlinkClicked);
            string strUrl = string.Format("https://www.virustotal.com/gui/search/{0}", strHash);
            WinUIHelper.OpenUrl(strUrl);
        }

        private void HandleRichTextSelectionScroll(ScrollViewer scrollViewerWrapper)
        {
            //string strDebug = "";

            // cursor position
            double scale = m_mainWindow.Scale;
            System.Drawing.Point pointCursor = m_mainWindow.GetCursorRelativePoint();

            //strDebug = string.Format("{0:0.00} : {1:0.00}", pointCursor.X, pointCursor.Y);
            //TextBlockDebug.Text = strDebug;

            // ScrollView position
            GeneralTransform transformScrollView = scrollViewerWrapper.TransformToVisual(null);
            Windows.Foundation.Point pointScrollView = transformScrollView.TransformPoint(new(0, 0));

            // cursor offset relative to ScrollView
            double cursorRelateScrollOffX = pointCursor.X - pointScrollView.X - (scrollViewerWrapper.Margin.Left * scale);
            double cursorRelateScrollOffY = pointCursor.Y - pointScrollView.Y - (scrollViewerWrapper.Margin.Top * scale);

            double scrollViewWidth = scrollViewerWrapper.ActualWidth * scale;
            double scrollViewHeight = scrollViewerWrapper.ActualHeight * scale;

            double cursorOutScrollWidthOffX = cursorRelateScrollOffX;
            if (cursorOutScrollWidthOffX > 0 && cursorOutScrollWidthOffX <= scrollViewWidth)
            {
                // X inside
                cursorOutScrollWidthOffX = 0;
            }
            else if (cursorOutScrollWidthOffX > scrollViewWidth)
            {
                // X outside right
                cursorOutScrollWidthOffX = cursorOutScrollWidthOffX - scrollViewWidth;
            }

            double cursorOutScrollHeightOffY = cursorRelateScrollOffY;
            if (cursorOutScrollHeightOffY > 0 && cursorOutScrollHeightOffY <= scrollViewHeight)
            {
                // Y inside
                cursorOutScrollHeightOffY = 0;
            }
            else if (cursorOutScrollHeightOffY > scrollViewHeight)
            {
                // Y outside right
                cursorOutScrollHeightOffY = cursorOutScrollHeightOffY - scrollViewHeight;
            }

            //strDebug = string.Format("{0:0.00} : {1:0.00}", cursorOutScrollWidthOffX, cursorOutScrollHeightOffY);

            if (cursorOutScrollWidthOffX == 0 && cursorOutScrollHeightOffY == 0)
            {
                // X and Y all inside
                //strDebug = string.Format("{0:0.00} : {1:0.00}", cursorOutScrollWidthOffX, cursorOutScrollHeightOffY);
                //TextBlockDebug.Text = strDebug;
                return;
            }

            double scrollViewCurOffX = scrollViewerWrapper.HorizontalOffset;
            double scrollViewCurOffY = scrollViewerWrapper.VerticalOffset;
            double scrollViewNewOffX = scrollViewCurOffX + cursorOutScrollWidthOffX;
            double scrollViewNewOffY = scrollViewCurOffY + cursorOutScrollHeightOffY;

            //strDebug = string.Format("{0:0.00} : {1:0.00}", scrollViewNewOffX, scrollViewNewOffY);
            WinUIHelper.ScrollViewerScrollTo(scrollViewerWrapper, scrollViewNewOffX, scrollViewNewOffY);

            //TextBlockDebug.Text = strDebug;
        }

        private void OnRedirected(string someArgs)
        {
            if (string.IsNullOrEmpty(someArgs))
                return;

            string[] splitArgs = CommandLineParser.SplitCommandLine(someArgs).ToArray();
            List<string> strFilePaths = [];
            bool foundPaths = false;
            for (int i = 0; i < splitArgs.Length; i++)
            {
                if (foundPaths)
                    strFilePaths.Add(splitArgs[i]);

                if (string.Equals(splitArgs[i], ArgPaths, StringComparison.OrdinalIgnoreCase))
                    foundPaths = true;
            }

            if (strFilePaths.Count > 0)
                StartHashCalc(strFilePaths);
        }

        private void GridMain_Loaded(object sender, RoutedEventArgs e)
        {
            if (!m_pageInited)
            {
                // Prepare RichTextMain
                RichTextMain.TextWrapping = TextWrapping.NoWrap;
                m_paragraphResult = CreateParagraphForTextMain();
                m_paragraphFind = CreateParagraphForTextMain();
                m_paragraphMain = m_paragraphResult;
                RichTextMain.Blocks.Add(m_paragraphMain);
                m_hyperlinksMain = m_hyperlinksResult;

                // Prepare controls
                ButtonOpen.Content = m_resourceLoaderMain.GetString("ButtonOpenOpen");
                TextBlockSpeed.Text = "";

                object objUppercase = WinUIHelper.LoadLocalSettings(KeyUppercase);
                CheckBoxUppercase.IsChecked = (bool)(objUppercase ?? false);
                UpdateUppercaseStat(false);

                // Load hash-type display filter and build the Hash menu.
                LoadDisplayHashTypes();
                InitHashMenuFlyout();

                // Init stat
                SetPageControlStat(MainPageControlStat.MainPageNone);

                // Handle commandline args
                DispatcherQueue.TryEnqueue(() =>
                {
                    AppActivationArguments appActiveArgs = WinUIHelper.GetCurrentActivatedEventArgs();
                    string strAppActiveArgs = WinUIHelper.GetLaunchActivatedEventArgs(appActiveArgs);
                    OnRedirected(strAppActiveArgs);
                });

                m_pageInited = true;
            }

            m_pageLoaded = true;

            if (m_pendingScrollToBottom)
            {
                m_pendingScrollToBottom = false;
                ScrollTextMainToBottom();
            }

            // Fix for color changed
            DispatcherQueueTimer timerScrollBar = DispatcherQueue.CreateTimer();
            timerScrollBar.Interval = TimeSpan.FromMilliseconds(300);
            timerScrollBar.IsRepeating = false;
            timerScrollBar.Tick += (timer, sender) =>
            {
                ScrollViewerMain.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                ScrollViewerMain.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            };
            timerScrollBar.Start();
        }

        private void GridMain_Unloaded(object sender, RoutedEventArgs e)
        {
            m_pageLoaded = false;
        }

        private void RichTextMainHyperlink_Click(Hyperlink sender, HyperlinkClickEventArgs args)
        {
            m_hyperlinkClicked = sender;
            ShowRichTextMainMenuFlyout();
        }

        private void RichTextMain_SelectionChanged(object sender, RoutedEventArgs e)
        {
            HandleRichTextSelectionScroll(ScrollViewerMain);
        }

        private void CheckBoxUppercase_Checked(object sender, RoutedEventArgs e)
        {
            UpdateResultUppercase();
        }

        private void CheckBoxUppercase_Unchecked(object sender, RoutedEventArgs e)
        {
            UpdateResultUppercase();
        }

        private void ButtonAbout_Click(object sender, RoutedEventArgs e)
        {
            // Fix for color changed
            ScrollViewerMain.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
            ScrollViewerMain.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;

            ShowAboutPage();
        }

        private void ButtonClear_Click(object sender, RoutedEventArgs e)
        {
            if (m_mainPageStat == MainPageControlStat.MainPageVerify)
                ClearFindResult();
            else
                SetPageControlStat(MainPageControlStat.MainPageNone);
        }

        private void ButtonVerify_Click(object sender, RoutedEventArgs e)
        {
            DispatcherQueue.TryEnqueue(ShowFindDialog);
        }

        private async void ButtonOpen_Click(object sender, RoutedEventArgs e)
        {
            if (m_mainPageStat == MainPageControlStat.MainPageCalcIng)
            {
                StopHashCalc(false);
            }
            else
            {
                FileOpenPicker picker = new();

                // Initialize the file picker with the window handle (HWND)
                InitializeWithWindow.Initialize(picker, m_mainWindow.HWNDHandle);

                // Set options for your file picker
                picker.FileTypeFilter.Add("*");

                // Open the picker for the user to pick a file
                IReadOnlyList<StorageFile> pickFiles = await picker.PickMultipleFilesAsync();
                if (pickFiles != null)
                {
                    // Application now has read/write access to the picked file
                    List<string> strPickFilePaths = [];
                    foreach (IStorageItem storageItem in pickFiles)
                    {
                        string path = storageItem.Path;
                        if (!string.IsNullOrEmpty(path))
                            strPickFilePaths.Add(path);
                    }

                    if (strPickFilePaths.Count == 0)
                        return;

                    DispatcherQueue.TryEnqueue(() => StartHashCalc(strPickFilePaths));
                }
            }
        }

        private void TextBoxFindHash_TextChanged(object sender, TextChangedEventArgs e)
        {
            string findString = m_textBoxFindHash.Text;

            // First, trim
            string fixedFindString = findString.Trim();

            // Second, regex
            string pattern = @"^(?:MD5|SHA(?:-?(?:1|256|512)))[\s:=]+(.+)$";
            Match match = Regex.Match(fixedFindString, pattern, RegexOptions.IgnoreCase);
            if (match.Success && match.Groups.Count >= 2)
            {
                fixedFindString = match.Groups[1].Value;
            }

            if (string.IsNullOrEmpty(fixedFindString) || fixedFindString == findString)
            {
                return; // no change
            }

            // Update the textbox without re-triggering the handler recursively
            m_textBoxFindHash.TextChanged -= TextBoxFindHash_TextChanged;
            try
            {
                m_textBoxFindHash.Text = fixedFindString;

                // Move caret to end
                m_textBoxFindHash.SelectionStart = fixedFindString.Length;
                m_textBoxFindHash.SelectionLength = 0;
            }
            finally
            {
                m_textBoxFindHash.TextChanged += TextBoxFindHash_TextChanged;
            }
        }

        private void UIBridgeHandlers_PreparingCalcHandler()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                string strPrepare = m_resourceLoaderMain.GetString("ResultWaitingStart");
                m_runPrepare = WinUIHelper.GenRunFromString(strPrepare);
                AppendInlineToTextMain(m_runPrepare);
            });
        }

        private void UIBridgeHandlers_RemovePreparingCalcHandler()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (m_runPrepare != null)
                {
                    m_runPrepare.Text = "";
                }
            });
        }

        private void UIBridgeHandlers_CalcStopHandler()
        {
            DispatcherQueue.TryEnqueue(CalculateStopped);
        }

        private void UIBridgeHandlers_CalcFinishHandler()
        {
            DispatcherQueue.TryEnqueue(CalculateFinished);
        }

        private void UIBridgeHandlers_ShowFileNameHandler(ResultDataNet resultData)
        {
            m_inMainQueue += 1;
            DispatcherQueue.TryEnqueue(() => AppendFileNameToTextMain(resultData));
        }

        private void UIBridgeHandlers_ShowFileMetaHandler(ResultDataNet resultData)
        {
            m_inMainQueue += 1;
            DispatcherQueue.TryEnqueue(() => AppendFileMetaToTextMain(resultData));
        }

        private void UIBridgeHandlers_ShowFileHashHandler(ResultDataNet resultData, bool uppercase)
        {
            m_inMainQueue += 1;
            DispatcherQueue.TryEnqueue(() =>
            {
                m_resultDataList.Add(resultData);
                AppendFileHashToTextMain(resultData, uppercase);
            });
        }

        private void UIBridgeHandlers_ShowFileErrHandler(ResultDataNet resultData)
        {
            m_inMainQueue += 1;
            DispatcherQueue.TryEnqueue(() =>
            {
                m_resultDataList.Add(resultData);
                AppendFileErrToTextMain(resultData);
            });
        }

        private void UIBridgeHandlers_UpdateProgWholeHandler(int value)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                double newValue = value;
                double oldValue = ProgressBarMain.Value;
                if (oldValue == newValue)
                    return;

                ProgressBarMain.Value = newValue;
                if (value == 0)
                    value = 1;
                m_mainWindow.SetTaskbarProgress((ulong)value);
            });
        }
    }
}
