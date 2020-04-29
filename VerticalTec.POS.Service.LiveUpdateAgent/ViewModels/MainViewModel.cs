﻿using Microsoft.AspNetCore.SignalR.Client;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using Prism.Regions;
using Prism.Services.Dialogs;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using VerticalTec.POS.Database;
using VerticalTec.POS.LiveUpdate;
using VerticalTec.POS.Service.LiveUpdateAgent.Events;

namespace VerticalTec.POS.Service.LiveUpdateAgent.ViewModels
{
    public class MainViewModel : BindableBase, INavigationAware
    {
        IEventAggregator _eventAggregator;
        IDatabase _db;
        IDialogService _dialogService;

        HubConnection _hubConnection;

        LiveUpdateDbContext _liveUpdateContext;
        POSDataSetting _posSetting;
        VtecPOSEnv _posEnv;

        VersionDeploy _lastDeploy;
        VersionLiveUpdate _versionLiveUpdate;

        bool _isBusy;

        bool _updateButtonEnable;
        ObservableCollection<string> _processInfoMessages;
        string _currentVersion;
        string _updateVersion;
        string _buttonText = "Start Update";

        public MainViewModel(IEventAggregator ea, IDatabase db, IDialogService dialogService,
            LiveUpdateDbContext liveupdateContext, FrontConfigManager frontConfig, VtecPOSEnv posEnv)
        {
            _eventAggregator = ea;
            _db = db;
            _dialogService = dialogService;
            _liveUpdateContext = liveupdateContext;
            _posSetting = frontConfig.POSDataSetting;
            _posEnv = posEnv;

            _processInfoMessages = new ObservableCollection<string>();
        }

        public string ButtonText
        {
            get => _buttonText;
            set => SetProperty(ref _buttonText, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        public bool UpdateButtonEnable
        {
            get => _updateButtonEnable;
            set => SetProperty(ref _updateButtonEnable, value);
        }

        public ObservableCollection<string> ProcessInfoMessages
        {
            get => _processInfoMessages;
            set => SetProperty(ref _processInfoMessages, value);
        }

        public string CurrentVersion
        {
            get => _currentVersion;
            set => SetProperty(ref _currentVersion, value);
        }

        public string UpdateVersion
        {
            get => _updateVersion;
            set => SetProperty(ref _updateVersion, value);
        }

        public ICommand StartUpdateCommand => new DelegateCommand(async () =>
        {
            await Task.Run(async () =>
            {
                _eventAggregator.GetEvent<VersionUpdateEvent>().Publish(UpdateEvents.Updating);

                UpdateInfoMessage("Extracting file...");
                UpdateButtonEnable = false;

                try
                {
                    var updateFilePath = _versionLiveUpdate.DownloadFilePath;
                    var posPath = _posEnv.FrontCashierPath;
                    var extractPath = Path.Combine(Path.GetTempPath(), $"vTec-ResPOS-{DateTime.Now.ToString("yyyyMMdd")}");
                    if (!Directory.Exists(extractPath))
                        Directory.CreateDirectory(extractPath);

                    var totalFile = 0;
                    using (var archive = ZipFile.OpenRead(updateFilePath))
                    {
                        totalFile = archive.Entries.Count();
                        foreach (var entry in archive.Entries)
                        {
                            if (entry.FullName.Equals("vTec-ResPOS.config", StringComparison.OrdinalIgnoreCase))
                                continue;

                            var destinationPath = Path.GetFullPath(Path.Combine(extractPath, entry.FullName));
                            if (string.IsNullOrEmpty(entry.Name))
                            {
                                if (!Directory.Exists(destinationPath))
                                    Directory.CreateDirectory(destinationPath);
                            }
                            else
                            {
                                entry.ExtractToFile(destinationPath, true);
                                UpdateInfoMessage($"Extract file {entry.Name}");
                            }
                        }
                    }

                    UpdateInfoMessage("Start copy file");
                    
                    // copy file from temp
                    foreach (string dirPath in Directory.GetDirectories(extractPath, "*", SearchOption.AllDirectories))
                    {
                        var destinationPath = dirPath.Replace(extractPath, posPath);
                        if (!Directory.Exists(destinationPath))
                            Directory.CreateDirectory(destinationPath);
                    }

                    foreach (string newPath in Directory.GetFiles(extractPath, "*.*", SearchOption.AllDirectories))
                    {
                        var destinationPath = newPath.Replace(extractPath, posPath);
                        File.Copy(newPath, destinationPath, true);
                        UpdateInfoMessage($"Copy file {destinationPath}");
                    }

                    try
                    {
                        Directory.Delete(extractPath);
                    }
                    catch { }

                    using (var conn = await _db.ConnectAsync())
                    {
                        //_lastDeploy.BatchStatus = 2;
                        _lastDeploy.UpdateDate = DateTime.Now;
                        await _liveUpdateContext.AddOrUpdateVersionDeploy(conn, _lastDeploy);

                        try
                        {
                            await _hubConnection?.InvokeAsync("UpdateVersionDeploy", _lastDeploy);
                        }
                        catch { }
                    }

                    ButtonText = "Done!";
                    UpdateButtonEnable = true;

                    UpdateInfoMessage($"Successfully");
                    _eventAggregator.GetEvent<VersionUpdateEvent>().Publish(UpdateEvents.UpdateSuccess);
                }
                catch (Exception ex)
                {
                    UpdateInfoMessage($"Extract file error {ex.Message}");
                    _eventAggregator.GetEvent<VersionUpdateEvent>().Publish(UpdateEvents.UpdateFail);
                }
            });
        });

        public async void OnNavigatedTo(NavigationContext navigationContext)
        {
            await GetVersionInfoAsync();
            await InitHubConnection();
        }

        async Task InitHubConnection()
        {
            try
            {
                using (var conn = await _db.ConnectAsync())
                {
                    var posRepo = new VtecPOSRepo(_db);
                    var liveUpdateHub = await posRepo.GetPropertyValueAsync(conn, 1050, "LiveUpdateHub");
                    if (!string.IsNullOrEmpty(liveUpdateHub))
                    {
                        _hubConnection = new HubConnectionBuilder()
                            .WithUrl(liveUpdateHub)
                            .WithAutomaticReconnect()
                            .Build();
                        _hubConnection.Closed += Closed;
                        await StartHubConnection();
                    }
                }
            }
            catch { }
        }

        async Task StartHubConnection(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                try
                {
                    await _hubConnection.StartAsync(cancellationToken);
                    break;
                }
                catch
                {
                    await Task.Delay(1000);
                }
            }
        }

        private Task Closed(Exception arg)
        {
            return StartHubConnection();
        }

        private async Task GetVersionInfoAsync()
        {
            try
            {
                IsBusy = true;
                //UpdateInfoMessage("Collecting version information...");
                using (var conn = await _db.ConnectAsync())
                {
                    var versionDeploys = await _liveUpdateContext.GetVersionDeploy(conn, _posSetting.ShopID);
                    _lastDeploy = versionDeploys.Where(v => v.BatchStatus == 1).OrderByDescending(v => v.UpdateDate).FirstOrDefault();
                    if (_lastDeploy != null)
                    {
                        _versionLiveUpdate = await _liveUpdateContext.GetVersionLiveUpdate(conn, _lastDeploy.BatchId, _lastDeploy.ShopId, _posSetting.ComputerID, ProgramTypes.Front);
                        if (_versionLiveUpdate != null)
                        {
                            var newVersionAvailable = _versionLiveUpdate.ReadyToUpdate == 1;

                            if (newVersionAvailable)
                            {
                                UpdateButtonEnable = newVersionAvailable;
                                UpdateVersion = _versionLiveUpdate.UpdateVersion;

                                UpdateInfoMessage($"New version {UpdateVersion} available");
                            }
                            else
                            {
                                UpdateInfoMessage($"No update available");
                                UpdateVersion = "-";
                            }

                            var versionInfo = await _liveUpdateContext.GetVersionInfo(conn, _lastDeploy.ShopId, _posSetting.ComputerID, ProgramTypes.Front);
                            CurrentVersion = versionInfo.FirstOrDefault()?.ProgramVersion;
                        }
                        else
                        {
                            UpdateInfoMessage("Not found version information!");
                        }
                    }
                    else
                    {
                        UpdateInfoMessage("Not found version information!");
                    }
                }
            }
            catch (Exception ex)
            {
                UpdateInfoMessage(ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        void UpdateInfoMessage(string message)
        {
            Application.Current.Dispatcher.Invoke(() => ProcessInfoMessages.Add(message));
        }

        public bool IsNavigationTarget(NavigationContext navigationContext)
        {
            return true;
        }

        public void OnNavigatedFrom(NavigationContext navigationContext)
        {
        }
    }
}
