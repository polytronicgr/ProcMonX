﻿using CsvHelper;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Prism.Commands;
using Prism.Mvvm;
using ProcMonX.Models;
using ProcMonX.Tracing;
using ProcMonX.ViewModels.Tabs;
using Syncfusion.Windows.Tools.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Zodiacon.WPF;

namespace ProcMonX.ViewModels {
    sealed class MainViewModel : BindableBase {
        public readonly TraceManager TraceManager = new TraceManager();
        ObservableCollection<TabItemViewModelBase> _tabs = new ObservableCollection<TabItemViewModelBase>();
        ObservableCollection<TraceEventDataViewModel> _events = new ObservableCollection<TraceEventDataViewModel>();
        Dictionary<string, TabItemViewModelBase> _views = new Dictionary<string, TabItemViewModelBase>(8);

        EventType[] _eventTypes;
        List<TraceEventDataViewModel> _tempEvents = new List<TraceEventDataViewModel>(8192);
        DispatcherTimer _updateTimer;
        CaptureViewModel _captureSettings;
        EventsViewModel _allEventsViewModel;

        public IList<TabItemViewModelBase> Tabs => _tabs;

        public EventType[] EventTypes => _eventTypes;

        public IList<TraceEventDataViewModel> Events => _events;

        public readonly IUIServices UI;

        public MainViewModel(IUIServices ui) {
            UI = ui;

            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
            Thread.CurrentThread.Priority = ThreadPriority.Highest;

            HookupEvents();

            _captureSettings = new CaptureViewModel(this);
            AddTab(_captureSettings, true);

            AddTab(_allEventsViewModel = new EventsViewModel(Events));
            _views.Add(_allEventsViewModel.Text, _allEventsViewModel);
            _views.Add(_captureSettings.Text, _captureSettings);

            _updateTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(1500), DispatcherPriority.Background, (_, __) => Update(), 
                Dispatcher.CurrentDispatcher);
            _updateTimer.Start();

        }

        private void HookupEvents() {
            var dispatcher = Dispatcher.CurrentDispatcher;
            TraceManager.EventTrace += (evt, type) => {
                var info = GetDetails(evt);
                var data = new TraceEventDataViewModel(evt, type, info);
                lock (_tempEvents)
                    _tempEvents.Add(data);
            };

        }

        private string GetDetails(TraceEvent evt) {
            switch (evt) {
                case ProcessTraceData data:
                    return $"Parent PID:;; {data.ParentID};; Flags:;; {data.Flags};; Image Path:;; {data.ImageFileName};; Command Line:;; {data.CommandLine}";

                case ThreadTraceData data:
                    return $"Win32 Start Address:;; 0x{data.Win32StartAddr:X};; Kernel Stack Base:;; 0x{data.StackBase:X}" + 
                        $" User Stack Base:;; 0x{data.UserStackBase:X};; TEB:;; 0x{data.TebBase:X};; Parent PID:;; {data.ParentProcessID}";

                case RegistryTraceData data:
                    return $"Key:;; {data.KeyName};; Value Name:;; {data.ValueName};; Status:;; 0x{data.Status:X};; Handle:;; 0x{data.KeyHandle:X}";

                case ImageLoadTraceData data:
                    return $"Name:;; {data.FileName};; Address:;; 0x{data.ImageBase:X};; Base:;; 0x{data.DefaultBase:X};; size:;; 0x{data.ImageSize:X}";

                case ALPCSendMessageTraceData data:
                    return $"Message ID: {data.MessageID}";

                case ALPCReceiveMessageTraceData alpc:
                    return $"Message ID: {alpc.MessageID}";

                case FileIOReadWriteTraceData data:
                    return $"Filename:;; {data.FileName};; Offset:;; {data.Offset:X};; Size:;; 0x{data.IoSize:X};; IRP:;; 0x{data.IrpPtr:X}";

                case VirtualAllocTraceData data:
                    return $"Address:;; 0x{data.BaseAddr:X};; Size:;; 0x{data.Length:X};; Flags:;; {(VirtualAllocFlags)(data.Flags)}";

                case TcpIpConnectTraceData data:
                    return $"Src Address:;; {data.saddr.ToString()};; Dst Address:;; {data.daddr.ToString()};; Dst Port:;; {data.dport};; Src Port:;; {data.sport};; Connection ID:;; {data.connid}";

                case TcpIpTraceData data:
                    return $"Src Address:;; {data.saddr.ToString()};; Dst Address:;; {data.daddr.ToString()};; Dst Port:;; {data.dport};; Src Port:;; {data.sport};; Size:;; {data.size};; Connection ID:;; {data.connid}";

                case TcpIpV6TraceData data:
                    return $"Src Address:;; {data.saddr.ToString()};; Dst Address:;; {data.daddr.ToString()};; Dst Port:;; {data.dport};; Src Port:;; {data.sport};; Size:;; {data.size};; Connection ID:;; {data.connid}";

                case TcpIpSendTraceData data:
                    return $"Src Address:;; {data.saddr.ToString()};; Dst Address:;; {data.daddr.ToString()};; Dst Port:;; {data.dport};; Src Port:;; {data.sport};;" + 
                        $" Size:;; {data.size};; Seq:;; {data.seqnum};; Start:;; {data.startime};; End:;; {data.endtime};; Connection ID:;; {data.connid}";

                case TcpIpV6SendTraceData data:
                    return $"Src Address:;; {data.saddr.ToString()};; Dst Address:;; {data.daddr.ToString()};; Dst Port:;; {data.dport};; Src Port:;; {data.sport};;" +
                        $" Size:;; {data.size};; Seq:;; {data.seqnum};; Start:;; {data.startime};; End:;; {data.endtime};; Connection ID:;; {data.connid}";

                case DiskIOTraceData data:
                    return $"Disk:;; {data.DiskNumber};; Offset:;; {data.ByteOffset};; Size:;; {data.TransferSize};; Priority:;; {data.Priority};; IRP:;;" + 
                        $" 0x{data.Irp:X};; IRP Flags:;; {data.IrpFlags};; File Key:;; 0x{data.FileKey:X};; Filename:;; {data.FileName}";
            }
            return string.Empty;
        }

        bool _suspendUpdates;

        public bool SuspendUpdates {
            get => _suspendUpdates;
            set {
                if (SetProperty(ref _suspendUpdates, value)) {
                    SuspendText = value ? "Suspending UI updates" : string.Empty;
                }
            }
        }

        string _suspendText;

        public string SuspendText {
            get => _suspendText;
            set => SetProperty(ref _suspendText, value);
        }

        private bool _isMonitoring;

        public bool IsMonitoring {
            get => _isMonitoring; 
            set => SetProperty(ref _isMonitoring, value);
        }

        private TabItemViewModelBase _selectedTab;

        public TabItemViewModelBase SelectedTab {
            get => _selectedTab; 
            set => SetProperty(ref _selectedTab, value); 
        }

        public TabItemViewModelBase AddTab(TabItemViewModelBase item, bool activate = false) {
            _tabs.Add(item);
            if (activate)
                SelectedTab = item;
            return item;
        }

        public void RemoveTab(TabItemViewModelBase tab) {
            _tabs.Remove(tab);
        }

        public ICommand ViewTabCommand => new DelegateCommand<string>(name => {
            if (_views.TryGetValue(name, out var view))
                SelectedTab = view;
            else {
                var tab = CreateTab(name);
                if (tab != null)
                    _views.Add(tab.Text, tab);
            }
        });

        private TabItemViewModelBase CreateTab(string name) {
            TabItemViewModelBase tab = null;
            switch (name) {
                case "Processes":
                    tab = new ProcessesViewModel(Events);
                    break;
            }

            if (tab != null)
                AddTab(tab, true);
            return tab;
        }

        public DelegateCommandBase GoCommand => new DelegateCommand(
            () => ResumeMonitoring(),
            () => !IsMonitoring)
            .ObservesProperty(() => IsMonitoring);

        public DelegateCommandBase StopCommand => new DelegateCommand(
            () => StopMonitoring(),
            () => IsMonitoring)
            .ObservesProperty(() => IsMonitoring).ObservesProperty(() => IsBusy);

        void Update() {
            _updateTimer.Stop();
            if (!SuspendUpdates) {
                lock (_tempEvents) {
                    int count = Math.Min(_tempEvents.Count, IsMonitoring ? 3072 : 8192);
                    for (int i = 0; i < count; i++)
                        _events.Add(_tempEvents[i]);
                    _tempEvents.RemoveRange(0, count);
                    IsBusy = _tempEvents.Count > 0;
                }
            }
            RaisePropertyChanged(nameof(LostEvents));
            RaisePropertyChanged(nameof(EventCount));

            _updateTimer.Start();
        }

        public string Title => "Process Monitor X v0.1 (C)2018 by Pavel Yosifovich";

        public ICommand ExitCommand => new DelegateCommand(() => Application.Current.Shutdown());

        public DelegateCommandBase ClearAllCommand => new DelegateCommand(() => _events.Clear());

        private bool _isBusy;

        public bool IsBusy {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        private async void StopMonitoring() {
            IsBusy = true;
            await Task.Run(() => TraceManager.Stop());
            IsBusy = false;
            IsMonitoring = false;
            SuspendUpdates = false;
        }

        private void ResumeMonitoring() {
            _eventTypes = _captureSettings.EventTypes.Where(type => type.IsMonitoring).Select(type => type.Info.EventType).ToArray();
            if (_eventTypes.Length == 0) {
                UI.MessageBoxService.ShowMessage("No events selected to monitor", App.Title, MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }
            if (SelectedTab == _captureSettings)
                SelectedTab = _allEventsViewModel;

            TraceManager.Start(EventTypes);
            IsMonitoring = true;

            foreach (var tab in _views.Values)
                tab.Refresh();
        }

        public int LostEvents => TraceManager.LostEvents;
        public int EventCount => _events.Count + _tempEvents.Count;

        public ICommand TabClosingCommand => new DelegateCommand<CancelingRoutedEventArgs>(args => {
            if (!SelectedTab.CanClose)
                args.Cancel = true;
        });

        public ICommand TabClosedCommand => new DelegateCommand<CloseTabEventArgs>(args => _views.Remove((args.TargetTabItem.DataContext as TabItemViewModelBase).Text));

        public ICommand SaveCommand => new DelegateCommand(() => {
            var filename = UI.FileDialogService.GetFileForSave("CSV Files (*.csv)|*.csv", "Select File");
            if (filename == null)
                return;

            SaveInternal(filename);
        }, () => !IsMonitoring).ObservesProperty(() => IsMonitoring);

        private void SaveInternal(string filename) {
            using (var writer = new StreamWriter(filename, append: false, encoding: Encoding.Unicode)) {
                using (var csvWriter = new CsvWriter(writer)) {
                    var data = _events.Select(evt => new EventData {
                        Index = evt.Index,
                        ProcessId = evt.ProcessId,
                        ProcessName = evt.ProcessName,
                        Details = evt.Details.Replace(";;", string.Empty),
                        ThreadId = evt.ThreadId,
                        CPU = evt.Processor,
                        Opcode = evt.Opcode,
                        EventType = evt.TypeAsString,
                        Category = evt.Category.ToString(),
                        Time = evt.TimeStamp
                    });

                    csvWriter.WriteHeader<EventData>();
                    csvWriter.NextRecord();
                    foreach (var evt in data) {
                        csvWriter.WriteRecord(evt);
                        csvWriter.NextRecord();
                    }
                }
            }
        }
    }
}