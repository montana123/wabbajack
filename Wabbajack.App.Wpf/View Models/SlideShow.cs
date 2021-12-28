﻿using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Windows.Media.Imaging;
using DynamicData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Wabbajack.Common;
using Wabbajack.DTOs.DownloadStates;
using Wabbajack.Lib;
using Wabbajack.Lib.Extensions;

namespace Wabbajack.View_Models
{
    public class SlideShow : ViewModel
    {
        private readonly Random _random = new Random();

        public InstallerVM Installer { get; }

        [Reactive]
        public bool ShowNSFW { get; set; }

        [Reactive]
        public bool Enable { get; set; } = true;

        private readonly ObservableAsPropertyHelper<BitmapImage> _image;
        public BitmapImage Image => _image.Value;

        private readonly ObservableAsPropertyHelper<ModVM> _targetMod;
        public ModVM TargetMod => _targetMod.Value;

        public ReactiveCommand<Unit, Unit> SlideShowNextItemCommand { get; } = ReactiveCommand.Create(() => { });
        public ReactiveCommand<Unit, Unit> VisitURLCommand { get; }

        public const int PreloadAmount = 4;

        public SlideShow(InstallerVM appState, IServiceProvider provider)
        {
            Installer = appState;

            // Wire target slideshow index
            var intervalSeconds = 10;
            // Compile all the sources that trigger a slideshow update, any of which trigger a counter update
            var selectedIndex = Observable.Merge(
                    // If user requests one manually
                    SlideShowNextItemCommand.StartingExecution(),
                    // If the natural timer fires
                    Observable.Merge(
                            // Start with an initial timer
                            Observable.Return(Observable.Interval(TimeSpan.FromSeconds(intervalSeconds))),
                            // but reset timer if user requests one
                            SlideShowNextItemCommand.StartingExecution()
                                .Select(_ => Observable.Interval(TimeSpan.FromSeconds(intervalSeconds))))
                        // When a new timer comes in, swap to it
                        .Switch()
                        .Unit()
                        // Only subscribe to timer if enabled and installing
                        .FlowSwitch(
                            Observable.CombineLatest(
                                this.WhenAny(x => x.Enable),
                                this.WhenAny(x => x.Installer.Installing),
                                resultSelector: (enabled, installing) => enabled && installing)))
                // When filter switch enabled, fire an initial signal
                .StartWith(Unit.Default)
                // Only subscribe to slideshow triggers if started
                .FlowSwitch(this.WhenAny(x => x.Installer.StartedInstallation))
                // Block spam
                .Debounce(TimeSpan.FromMilliseconds(250), RxApp.MainThreadScheduler)
                .Scan(
                    seed: 0,
                    accumulator: (i, _) => i + 1)
                .Publish()
                .RefCount();

            // Dynamic list changeset of mod VMs to display
            var modVMs =  this.WhenAny(x => x.Installer.ModList)
                // Whenever modlist changes, grab the list of its slides
                .Select(modList =>
                {
                    if (modList?.SourceModList?.Archives == null)
                    {
                        return Observable.Empty<IMetaState>()
                            .ToObservableChangeSet(x => x.LinkUrl);
                    }
                    return modList.SourceModList.Archives
                        .Select(m => m.State)
                        .OfType<IMetaState>()
                        .Where(x => x.LinkUrl != default && x.ImageURL != default)
                        .DistinctBy(x => x.LinkUrl)
                        // Shuffle it
                        .Shuffle(_random)
                        .AsObservableChangeSet(x => x.LinkUrl);
                })
                // Switch to the new list after every ModList change
                .Switch()
                .Transform(mod => new ModVM(provider.GetRequiredService<ILogger<ModVM>>(), mod))
                .DisposeMany()
                // Filter out any NSFW slides if we don't want them
                .AutoRefreshOnObservable(slide => this.WhenAny(x => x.ShowNSFW))
                .Filter(slide => !slide.State.IsNSFW || ShowNSFW)
                .RefCount();

            // Find target mod to display by combining dynamic list with currently desired index
            _targetMod = Observable.CombineLatest(
                    modVMs.QueryWhenChanged(),
                    selectedIndex,
                    resultSelector: (query, selected) =>
                    {
                        var index = selected % (query.Count == 0 ? 1 : query.Count);
                        return query.Items.ElementAtOrDefault(index);
                    })
                .StartWith(default(ModVM))
                .ToGuiProperty(this, nameof(TargetMod));

            // Mark interest and materialize image of target mod
            _image = this.WhenAny(x => x.TargetMod)
                // We want to Switch here, not SelectMany, as we want to hotswap to newest target without waiting on old ones
                .Select(x => x?.ImageObservable ?? Observable.Return(default(BitmapImage)))
                .Switch()
                .ToGuiProperty(this, nameof(Image));

            VisitURLCommand = ReactiveCommand.Create(
                execute: () =>
                {
                    UIUtils.OpenWebsite(TargetMod.State.LinkUrl);
                    return Unit.Default;
                },
                canExecute: this.WhenAny(x => x.TargetMod.State.LinkUrl)
                    .Select(x =>
                    {
                        //var regex = new Regex("^(http|https):\\/\\/");
                        var scheme = x?.Scheme;
                        return scheme != null && 
                               (scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
                                scheme.Equals("http", StringComparison.OrdinalIgnoreCase));
                    })
                    .ObserveOnGuiThread());

            // Preload upcoming images
            var list = Observable.CombineLatest(
                    modVMs.QueryWhenChanged(),
                    selectedIndex,
                    resultSelector: (query, selected) =>
                    {
                        // Retrieve the mods that should be preloaded
                        var index = selected % (query.Count == 0 ? 1 : query.Count);
                        var amountToTake = Math.Min(query.Count - index, PreloadAmount);
                        return query.Items.Skip(index).Take(amountToTake).ToObservable();
                    })
                .Select(i => i.ToObservableChangeSet())
                .Switch()
                .Transform(mod => mod.ImageObservable.Subscribe())
                .DisposeMany()
                .AsObservableList();
        }
    }
}
