// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Screens;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests;
using osu.Game.Online.Spectator;
using osu.Game.Overlays.BeatmapListing.Panels;
using osu.Game.Overlays.Settings;
using osu.Game.Replays;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Replays;
using osu.Game.Rulesets.Replays.Types;
using osu.Game.Scoring;
using osu.Game.Screens.Multi.Match.Components;
using osu.Game.Users;
using osuTK;

namespace osu.Game.Screens.Play
{
    public class Spectator : OsuScreen
    {
        private readonly User targetUser;

        [Resolved]
        private Bindable<WorkingBeatmap> beatmap { get; set; }

        [Resolved]
        private Bindable<RulesetInfo> ruleset { get; set; }

        private Ruleset rulesetInstance;

        [Resolved]
        private Bindable<IReadOnlyList<Mod>> mods { get; set; }

        [Resolved]
        private IAPIProvider api { get; set; }

        [Resolved]
        private SpectatorStreamingClient spectatorStreaming { get; set; }

        [Resolved]
        private BeatmapManager beatmaps { get; set; }

        [Resolved]
        private RulesetStore rulesets { get; set; }

        private Replay replay;

        private Container beatmapPanelContainer;

        private SpectatorState state;

        private IBindable<WeakReference<BeatmapSetInfo>> managerUpdated;

        private TriangleButton watchButton;

        private SettingsCheckbox automaticDownload;

        private BeatmapSetInfo onlineBeatmap;

        /// <summary>
        /// Becomes true if a new state is waiting to be loaded (while this screen was not active).
        /// </summary>
        private bool newStatePending;

        public Spectator([NotNull] User targetUser)
        {
            this.targetUser = targetUser ?? throw new ArgumentNullException(nameof(targetUser));
        }

        [BackgroundDependencyLoader]
        private void load(OsuColour colours, OsuConfigManager config)
        {
            InternalChild = new Container
            {
                Masking = true,
                CornerRadius = 20,
                AutoSizeAxes = Axes.Both,
                AutoSizeDuration = 500,
                AutoSizeEasing = Easing.OutQuint,
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Children = new Drawable[]
                {
                    new Box
                    {
                        Colour = colours.GreySeafoamDark,
                        RelativeSizeAxes = Axes.Both,
                    },
                    new FillFlowContainer
                    {
                        Margin = new MarginPadding(20),
                        AutoSizeAxes = Axes.Both,
                        Direction = FillDirection.Vertical,
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Spacing = new Vector2(15),
                        Children = new Drawable[]
                        {
                            new OsuSpriteText
                            {
                                Text = "Spectator Mode",
                                Font = OsuFont.Default.With(size: 30),
                                Anchor = Anchor.Centre,
                                Origin = Anchor.Centre,
                            },
                            new FillFlowContainer
                            {
                                AutoSizeAxes = Axes.Both,
                                Direction = FillDirection.Horizontal,
                                Anchor = Anchor.Centre,
                                Origin = Anchor.Centre,
                                Spacing = new Vector2(15),
                                Children = new Drawable[]
                                {
                                    new UserGridPanel(targetUser)
                                    {
                                        Anchor = Anchor.CentreLeft,
                                        Origin = Anchor.CentreLeft,
                                        Height = 145,
                                        Width = 290,
                                    },
                                    new SpriteIcon
                                    {
                                        Size = new Vector2(40),
                                        Icon = FontAwesome.Solid.ArrowRight,
                                        Anchor = Anchor.CentreLeft,
                                        Origin = Anchor.CentreLeft,
                                    },
                                    beatmapPanelContainer = new Container
                                    {
                                        AutoSizeAxes = Axes.Both,
                                        Anchor = Anchor.CentreLeft,
                                        Origin = Anchor.CentreLeft,
                                    },
                                }
                            },
                            automaticDownload = new SettingsCheckbox
                            {
                                LabelText = "Automatically download beatmaps",
                                Current = config.GetBindable<bool>(OsuSetting.AutomaticallyDownloadWhenSpectating),
                                Anchor = Anchor.Centre,
                                Origin = Anchor.Centre,
                            },
                            watchButton = new PurpleTriangleButton
                            {
                                Text = "Start Watching",
                                Width = 250,
                                Anchor = Anchor.Centre,
                                Origin = Anchor.Centre,
                                Action = attemptStart,
                                Enabled = { Value = false }
                            }
                        }
                    }
                }
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            spectatorStreaming.OnUserBeganPlaying += userBeganPlaying;
            spectatorStreaming.OnUserFinishedPlaying += userFinishedPlaying;
            spectatorStreaming.OnNewFrames += userSentFrames;

            spectatorStreaming.WatchUser((int)targetUser.Id);

            managerUpdated = beatmaps.ItemUpdated.GetBoundCopy();
            managerUpdated.BindValueChanged(beatmapUpdated);

            automaticDownload.Current.BindValueChanged(_ => checkForAutomaticDownload());
        }

        private void beatmapUpdated(ValueChangedEvent<WeakReference<BeatmapSetInfo>> beatmap)
        {
            if (beatmap.NewValue.TryGetTarget(out var beatmapSet) && beatmapSet.Beatmaps.Any(b => b.OnlineBeatmapID == state.BeatmapID))
                Schedule(attemptStart);
        }

        private void userSentFrames(int userId, FrameDataBundle data)
        {
            if (userId != targetUser.Id)
                return;

            // this should never happen as the server sends the user's state on watching,
            // but is here as a safety measure.
            if (replay == null)
                return;

            foreach (var frame in data.Frames)
            {
                IConvertibleReplayFrame convertibleFrame = rulesetInstance.CreateConvertibleReplayFrame();
                convertibleFrame.FromLegacy(frame, beatmap.Value.Beatmap);

                var convertedFrame = (ReplayFrame)convertibleFrame;
                convertedFrame.Time = frame.Time;

                replay.Frames.Add(convertedFrame);
            }
        }

        private void userBeganPlaying(int userId, SpectatorState state)
        {
            if (userId != targetUser.Id)
                return;

            this.state = state;

            if (this.IsCurrentScreen())
                Schedule(attemptStart);
            else
                newStatePending = true;
        }

        public override void OnResuming(IScreen last)
        {
            base.OnResuming(last);

            if (newStatePending)
            {
                attemptStart();
                newStatePending = false;
            }
        }

        private void userFinishedPlaying(int userId, SpectatorState state)
        {
            if (userId != targetUser.Id)
                return;

            if (replay != null)
            {
                replay.HasReceivedAllFrames = true;
                replay = null;
            }

            Schedule(clearDisplay);
        }

        private void clearDisplay()
        {
            watchButton.Enabled.Value = false;
            beatmapPanelContainer.Clear();
        }

        private void attemptStart()
        {
            clearDisplay();
            showBeatmapPanel(state);

            var resolvedRuleset = rulesets.AvailableRulesets.FirstOrDefault(r => r.ID == state.RulesetID)?.CreateInstance();

            // ruleset not available
            if (resolvedRuleset == null)
                return;

            if (state.BeatmapID == null)
                return;

            var resolvedBeatmap = beatmaps.QueryBeatmap(b => b.OnlineBeatmapID == state.BeatmapID);

            if (resolvedBeatmap == null)
            {
                return;
            }

            replay ??= new Replay { HasReceivedAllFrames = false };

            var scoreInfo = new ScoreInfo
            {
                Beatmap = resolvedBeatmap,
                User = targetUser,
                Mods = state.Mods.Select(m => m.ToMod(resolvedRuleset)).ToArray(),
                Ruleset = resolvedRuleset.RulesetInfo,
            };

            ruleset.Value = resolvedRuleset.RulesetInfo;
            rulesetInstance = resolvedRuleset;

            beatmap.Value = beatmaps.GetWorkingBeatmap(resolvedBeatmap);
            watchButton.Enabled.Value = true;

            this.Push(new SpectatorPlayerLoader(new Score
            {
                ScoreInfo = scoreInfo,
                Replay = replay,
            }));
        }

        private void showBeatmapPanel(SpectatorState state)
        {
            if (state?.BeatmapID == null)
            {
                beatmapPanelContainer.Clear();
                onlineBeatmap = null;
                return;
            }

            var req = new GetBeatmapSetRequest(state.BeatmapID.Value, BeatmapSetLookupType.BeatmapId);
            req.Success += res => Schedule(() =>
            {
                if (state != this.state)
                    return;

                onlineBeatmap = res.ToBeatmapSet(rulesets);
                beatmapPanelContainer.Child = new GridBeatmapPanel(onlineBeatmap);
                checkForAutomaticDownload();
            });

            api.Queue(req);
        }

        private void checkForAutomaticDownload()
        {
            if (onlineBeatmap == null)
                return;

            if (!automaticDownload.Current.Value)
                return;

            if (beatmaps.IsAvailableLocally(onlineBeatmap))
                return;

            beatmaps.Download(onlineBeatmap);
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            if (spectatorStreaming != null)
            {
                spectatorStreaming.OnUserBeganPlaying -= userBeganPlaying;
                spectatorStreaming.OnUserFinishedPlaying -= userFinishedPlaying;
                spectatorStreaming.OnNewFrames -= userSentFrames;

                spectatorStreaming.StopWatchingUser((int)targetUser.Id);
            }

            managerUpdated?.UnbindAll();
        }
    }
}
