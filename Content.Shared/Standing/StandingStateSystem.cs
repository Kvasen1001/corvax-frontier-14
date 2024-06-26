using Content.Shared.Actions;
using Content.Shared.Buckle;
using Content.Shared.Buckle.Components;
using Content.Shared.Hands.Components;
using Content.Shared.LieDown;
using Robust.Shared.Timing;
using Content.Shared.Movement.Systems;
using Content.Shared.Physics;
using Content.Shared.Rotation;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;

namespace Content.Shared.Standing
{
    public sealed class StandingStateSystem : EntitySystem
    {
        [Dependency] private readonly IGameTiming _timing = default!;
        [Dependency] private readonly SharedAppearanceSystem _appearance = default!;
        [Dependency] private readonly SharedAudioSystem _audio = default!;
        [Dependency] private readonly SharedPhysicsSystem _physics = default!;
        [Dependency] private readonly SharedActionsSystem _actions = default!;
        [Dependency] private readonly SharedLieDownSystem _lieDown = default!;
        [Dependency] private readonly ActionContainerSystem _actionContainer = default!;
        [Dependency] private readonly SharedBuckleSystem _buckle = default!;
        // If StandingCollisionLayer value is ever changed to more than one layer, the logic needs to be edited.
        private const int StandingCollisionLayer = (int)CollisionGroup.MidImpassable;

        public override void Initialize()
        {
            SubscribeLocalEvent<StandingStateComponent, ComponentStartup>(OnComponentInit);
            SubscribeLocalEvent<StandingStateComponent, LieDownActionEvent>(OnLieDownAction);
            SubscribeLocalEvent<StandingStateComponent, StandUpActionEvent>(OnStandUpAction);
            SubscribeLocalEvent<StandingStateComponent, MapInitEvent>(OnMapInit);
        }

        public bool IsDown(EntityUid uid, StandingStateComponent? standingState = null, LyingDownComponent? liedownComp = null)
        {
            if (!Resolve(uid, ref standingState, false))
                return false;

            return Resolve(uid, ref liedownComp, false);
        }

        private void OnMapInit(EntityUid uid, StandingStateComponent component, MapInitEvent args)
        {
            _actionContainer.EnsureAction(uid, ref component.LieDownActionEntity, component.LieDownAction);
            _actionContainer.EnsureAction(uid, ref component.StandUpActionEntity, component.StandUpAction);
            Dirty(uid, component);
        }

        /// <summary>
        ///     When component is added to player, add an action.
        /// </summary>
        private void OnComponentInit(EntityUid uid, StandingStateComponent component, ComponentStartup args)
        {
            _actions.AddAction(uid, ref component.LieDownActionEntity, component.LieDownAction);
        }

        /// <summary>
        ///     Event that being risen on lie down attempt.
        /// </summary>
        private void OnLieDownAction(EntityUid uid, StandingStateComponent component, LieDownActionEvent args)
        {
            if (!_buckle.IsBuckled(uid))
                _lieDown.TryLieDown(uid);
        }

        /// <summary>
        ///     Event that being risen on stand up attempt.
        /// </summary>
        private void OnStandUpAction(EntityUid uid, StandingStateComponent? component, StandUpActionEvent args)
        {
            if (!_buckle.IsBuckled(uid))
                _lieDown.TryStandUp(uid);
        }

        private bool DelayCheck(EntityUid uid, StandingStateComponent? standingState = null)
        {
            if (!Resolve(uid, ref standingState, false))
                return false;

            if (standingState.LastUsage + standingState.Delay > _timing.CurTime)
                return false;

            standingState.LastUsage = _timing.CurTime;
            return true;
        }

        public bool Down(EntityUid uid, bool playSound = true, bool dropHeldItems = true,
            StandingStateComponent? standingState = null,
            AppearanceComponent? appearance = null,
            HandsComponent? hands = null)
        {
            // TODO: This should actually log missing comps...
            if (!Resolve(uid, ref standingState, false))
                return false;

            if (!DelayCheck(uid, standingState))
                return false;
            // Optional component.
            Resolve(uid, ref appearance, ref hands, false);

            if (IsDown(uid))
                return true;

            // This is just to avoid most callers doing this manually saving boilerplate
            // 99% of the time you'll want to drop items but in some scenarios (e.g. buckling) you don't want to.
            // We do this BEFORE downing because something like buckle may be blocking downing but we want to drop hand items anyway
            // and ultimately this is just to avoid boilerplate in Down callers + keep their behavior consistent.
            if (dropHeldItems && hands != null)
            {
                RaiseLocalEvent(uid, new DropHandItemsEvent(), false);
            }

            var msg = new DownAttemptEvent();
            RaiseLocalEvent(uid, msg, false);

            if (msg.Cancelled)
                return false;

            EnsureComp<LyingDownComponent>(uid);
            Dirty(uid, standingState);
            RaiseLocalEvent(uid, new DownedEvent(), false);

            // Seemed like the best place to put it
            _appearance.SetData(uid, RotationVisuals.RotationState, RotationState.Horizontal, appearance);

            // Change collision masks to allow going under certain entities like flaps and tables
            if (TryComp(uid, out FixturesComponent? fixtureComponent))
            {
                foreach (var (key, fixture) in fixtureComponent.Fixtures)
                {
                    if ((fixture.CollisionMask & StandingCollisionLayer) == 0)
                        continue;

                    standingState.ChangedFixtures.Add(key);
                    _physics.SetCollisionMask(uid, key, fixture, fixture.CollisionMask & ~StandingCollisionLayer, manager: fixtureComponent);
                }
            }

            // check if component was just added or streamed to client
            // if true, no need to play sound - mob was down before player could seen that
            if (standingState.LifeStage <= ComponentLifeStage.Starting)
                return true;

            if (playSound)
            {
                _audio.PlayPredicted(standingState.DownSound, uid, uid);
            }

            return true;
        }

        public bool Stand(EntityUid uid,
            StandingStateComponent? standingState = null,
            AppearanceComponent? appearance = null,
            bool force = false)
        {
            // TODO: This should actually log missing comps...
            if (!Resolve(uid, ref standingState, false))
                return false;

            if (!DelayCheck(uid, standingState))
                return false;

            // Optional component.
            Resolve(uid, ref appearance, false);

            if (!IsDown(uid))
                return true;

            if (!force)
            {
                var msg = new StandAttemptEvent();
                RaiseLocalEvent(uid, msg, false);

                if (msg.Cancelled)
                    return false;
            }

            RemCompDeferred<LyingDownComponent>(uid);
            Dirty(uid, standingState);
            RaiseLocalEvent(uid, new StoodEvent(), false);

            _appearance.SetData(uid, RotationVisuals.RotationState, RotationState.Vertical, appearance);

            if (TryComp(uid, out FixturesComponent? fixtureComponent))
            {
                foreach (var key in standingState.ChangedFixtures)
                {
                    if (fixtureComponent.Fixtures.TryGetValue(key, out var fixture))
                        _physics.SetCollisionMask(uid, key, fixture, fixture.CollisionMask | StandingCollisionLayer, fixtureComponent);
                }
            }
            standingState.ChangedFixtures.Clear();

            LyingDownComponent? liedownComp = null;
            //if (Resolve(uid, ref liedownComp, false)) {
            //    RemCompDeferred<LyingDownComponent>(uid);
            //}

            return true;
        }
    }

    public sealed class DropHandItemsEvent : EventArgs
    {
    }

    /// <summary>
    /// Subscribe if you can potentially block a down attempt.
    /// </summary>
    public sealed class DownAttemptEvent : CancellableEntityEventArgs
    {
    }

    /// <summary>
    /// Subscribe if you can potentially block a stand attempt.
    /// </summary>
    public sealed class StandAttemptEvent : CancellableEntityEventArgs
    {
    }

    /// <summary>
    /// Raised when an entity becomes standing
    /// </summary>
    public sealed class StoodEvent : EntityEventArgs
    {
    }

    /// <summary>
    /// Raised when an entity is not standing
    /// </summary>
    public sealed class DownedEvent : EntityEventArgs
    {
    }
}
