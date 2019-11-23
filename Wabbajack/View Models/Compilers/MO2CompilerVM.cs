﻿using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Wabbajack.Common;
using Wabbajack.Lib;

namespace Wabbajack
{
    public class MO2CompilerVM : ViewModel, ISubCompilerVM
    {
        private readonly MO2CompilationSettings _settings;

        private readonly ObservableAsPropertyHelper<string> _mo2Folder;
        public string Mo2Folder => _mo2Folder.Value;

        private readonly ObservableAsPropertyHelper<string> _moProfile;
        public string MOProfile => _moProfile.Value;

        public FilePickerVM DownloadLocation { get; }

        public FilePickerVM ModlistLocation { get; }

        public IReactiveCommand BeginCommand { get; }

        [Reactive]
        public ACompiler ActiveCompilation { get; private set; }

        private readonly ObservableAsPropertyHelper<ModlistSettingsEditorVM> _modlistSettings;
        public ModlistSettingsEditorVM ModlistSettings => _modlistSettings.Value;

        [Reactive]
        public StatusUpdateTracker StatusTracker { get; private set; }

        public MO2CompilerVM(CompilerVM parent)
        {
            ModlistLocation = new FilePickerVM()
            {
                ExistCheckOption = FilePickerVM.ExistCheckOptions.On,
                PathType = FilePickerVM.PathTypeOptions.File,
                PromptTitle = "Select Modlist"
            };
            DownloadLocation = new FilePickerVM()
            {
                ExistCheckOption = FilePickerVM.ExistCheckOptions.On,
                PathType = FilePickerVM.PathTypeOptions.Folder,
                PromptTitle = "Select Download Location",
            };

            _mo2Folder = this.WhenAny(x => x.ModlistLocation.TargetPath)
                .Select(loc =>
                {
                    try
                    {
                        var profileFolder = Path.GetDirectoryName(loc);
                        return Path.GetDirectoryName(Path.GetDirectoryName(profileFolder));
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                })
                .ToProperty(this, nameof(Mo2Folder));
            _moProfile = this.WhenAny(x => x.ModlistLocation.TargetPath)
                .Select(loc =>
                {
                    try
                    {
                        var profileFolder = Path.GetDirectoryName(loc);
                        return Path.GetFileName(profileFolder);
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                })
                .ToProperty(this, nameof(MOProfile));

            // Wire missing Mo2Folder to signal error state for Modlist Location
            ModlistLocation.AdditionalError = this.WhenAny(x => x.Mo2Folder)
                .Select<string, IErrorResponse>(moFolder =>
                {
                    if (Directory.Exists(moFolder)) return ErrorResponse.Success;
                    return ErrorResponse.Fail($"MO2 Folder could not be located from the given modlist location.{Environment.NewLine}Make sure your modlist is inside a valid MO2 distribution.");
                });

            // Wire start command
            BeginCommand = ReactiveCommand.CreateFromTask(
                canExecute: Observable.CombineLatest(
                        this.WhenAny(x => x.ModlistLocation.InError),
                        this.WhenAny(x => x.DownloadLocation.InError),
                        resultSelector: (ml, down) => !ml && !down)
                    .ObserveOnGuiThread(),
                execute: async () =>
                {
                    try
                    {
                        ActiveCompilation = new MO2Compiler(Mo2Folder)
                        {
                            MO2Profile = MOProfile,
                            ModListName = ModlistSettings.ModListName,
                            ModListAuthor = ModlistSettings.AuthorText,
                            ModListDescription = ModlistSettings.Description,
                            ModListImage = ModlistSettings.ImagePath.TargetPath,
                            ModListWebsite = ModlistSettings.Website,
                            ModListReadme = ModlistSettings.ReadMeText.TargetPath,
                        };
                    }
                    catch (Exception ex)
                    {
                        while (ex.InnerException != null) ex = ex.InnerException;
                        Utils.Log($"Compiler error: {ex.ExceptionToString()}");
                        return;
                    }

                    try
                    {
                        await ActiveCompilation.Begin();
                    }
                    catch (Exception ex)
                    {
                        while (ex.InnerException != null) ex = ex.InnerException;
                        Utils.Log($"Compiler error: {ex.ExceptionToString()}");
                    }
                    finally
                    {
                        StatusTracker = null;
                        ActiveCompilation.Dispose();
                        ActiveCompilation = null;
                    }
                    
                });

            // Load settings
            _settings = parent.MWVM.Settings.Compiler.MO2Compilation;
            ModlistLocation.TargetPath = _settings.LastCompiledProfileLocation;
            if (!string.IsNullOrWhiteSpace(_settings.DownloadLocation))
            {
                DownloadLocation.TargetPath = _settings.DownloadLocation;
            }
            parent.MWVM.Settings.SaveSignal
                .Subscribe(_ => Unload())
                .DisposeWith(CompositeDisposable);

            // Load custom modlist settings per MO2 profile
            _modlistSettings = Observable.CombineLatest(
                    this.WhenAny(x => x.ModlistLocation.ErrorState),
                    this.WhenAny(x => x.ModlistLocation.TargetPath),
                    resultSelector: (state, path) => (State: state, Path: path))
                // A short throttle is a quick hack to make the above changes "atomic"
                .Throttle(TimeSpan.FromMilliseconds(25))
                .Select(u =>
                {
                    if (u.State.Failed) return null;
                    var modlistSettings = _settings.ModlistSettings.TryCreate(u.Path);
                    return new ModlistSettingsEditorVM(modlistSettings)
                    {
                        ModListName = MOProfile
                    };
                })
                // Interject and save old while loading new
                .Pairwise()
                .Do(pair =>
                {
                    pair.Previous?.Save();
                    pair.Current?.Init();
                })
                .Select(x => x.Current)
                // Save to property
                .ObserveOnGuiThread()
                .ToProperty(this, nameof(ModlistSettings));

            // If Mo2 folder changes and download location is empty, set it for convenience
            this.WhenAny(x => x.Mo2Folder)
                .DelayInitial(TimeSpan.FromMilliseconds(100))
                .Where(x => Directory.Exists(x))
                .FilterSwitch(
                    this.WhenAny(x => x.DownloadLocation.Exists)
                        .Invert())
                .Subscribe(x =>
                {
                    try
                    {
                        var tmpCompiler = new MO2Compiler(Mo2Folder);
                        DownloadLocation.TargetPath = tmpCompiler.MO2DownloadsFolder;
                    }
                    catch (Exception ex)
                    {
                        Utils.Log($"Error setting default download location {ex}");
                    }
                })
                .DisposeWith(CompositeDisposable);
        }

        public void Unload()
        {
            _settings.DownloadLocation = DownloadLocation.TargetPath;
            _settings.LastCompiledProfileLocation = ModlistLocation.TargetPath;
            ModlistSettings?.Save();
        }
    }
}