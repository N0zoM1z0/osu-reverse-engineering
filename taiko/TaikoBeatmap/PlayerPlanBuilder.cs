namespace OsuReverseEngineering.Taiko;

public static class PlayerPlanBuilder
{
    private static readonly TaikoKey[] SpinnerCycle =
    {
        TaikoKey.InnerLeft,
        TaikoKey.OuterLeft,
        TaikoKey.InnerRight,
        TaikoKey.OuterRight
    };

    public static TaikoPlayerPlan Build(
        TaikoBeatmapDocument beatmap,
        int tapMilliseconds = 8,
        int drumRollIntervalMilliseconds = 0,
        int spinnerIntervalMilliseconds = 0,
        TaikoGameplayModifiers modifiers = TaikoGameplayModifiers.None)
    {
        ArgumentNullException.ThrowIfNull(beatmap);
        ValidateRange(tapMilliseconds, 1, 100, nameof(tapMilliseconds));
        ValidateIntervalOverride(drumRollIntervalMilliseconds, nameof(drumRollIntervalMilliseconds));
        ValidateIntervalOverride(spinnerIntervalMilliseconds, nameof(spinnerIntervalMilliseconds));
        ValidateModifiers(modifiers);

        var strikes = new List<TaikoStrike>();
        var preferLeftHand = true;
        foreach (var hitObject in beatmap.HitObjects)
        {
            switch (hitObject.Kind)
            {
                case TaikoObjectKind.Circle:
                    strikes.Add(BuildCircleStrike(hitObject, preferLeftHand));
                    break;

                case TaikoObjectKind.DrumRoll:
                    AddDrumRollStrikes(
                        strikes,
                        beatmap,
                        hitObject,
                        drumRollIntervalMilliseconds,
                        ref preferLeftHand);
                    break;

                case TaikoObjectKind.Spinner:
                    AddSpinnerStrikes(strikes, beatmap, hitObject, spinnerIntervalMilliseconds, modifiers);
                    break;

                default:
                    throw new InvalidOperationException($"Unhandled Taiko object kind {hitObject.Kind}.");
            }
            preferLeftHand = !preferLeftHand;
        }

        strikes.Sort((left, right) =>
        {
            var byTime = left.Time.CompareTo(right.Time);
            if (byTime != 0)
                return byTime;
            if (left.RequiredForCombo != right.RequiredForCombo)
                return left.RequiredForCombo ? -1 : 1;
            return left.SourceLine.CompareTo(right.SourceLine);
        });

        var transitions = BuildTransitions(strikes, tapMilliseconds);
        ValidateTransitions(transitions);
        return new TaikoPlayerPlan
        {
            Path = beatmap.Path,
            Strikes = strikes,
            Transitions = transitions
        };
    }

    private static TaikoStrike BuildCircleStrike(TaikoHitObject hitObject, bool preferLeftHand)
    {
        IReadOnlyList<TaikoKey> keys;
        if (hitObject.Colour == TaikoColour.Don)
        {
            keys = hitObject.IsStrong
                ? new[] { TaikoKey.InnerLeft, TaikoKey.InnerRight }
                : new[] { preferLeftHand ? TaikoKey.InnerLeft : TaikoKey.InnerRight };
        }
        else
        {
            keys = hitObject.IsStrong
                ? new[] { TaikoKey.OuterLeft, TaikoKey.OuterRight }
                : new[] { preferLeftHand ? TaikoKey.OuterLeft : TaikoKey.OuterRight };
        }

        return new TaikoStrike
        {
            Time = hitObject.StartTime,
            Keys = keys,
            SourceKind = hitObject.Kind,
            SourceLine = hitObject.SourceLine,
            RequiredForCombo = true
        };
    }

    private static void AddDrumRollStrikes(
        ICollection<TaikoStrike> strikes,
        TaikoBeatmapDocument beatmap,
        TaikoHitObject hitObject,
        int intervalOverride,
        ref bool preferLeftHand)
    {
        var interval = intervalOverride > 0
            ? intervalOverride
            : ResolveNativeDrumRollInterval(beatmap, hitObject);
        var previousTime = int.MinValue;
        for (var exactTime = (double)hitObject.StartTime; exactTime < hitObject.EndTime; exactTime += interval)
        {
            // The game's Auto path accumulates a double and truncates each frame time.
            var time = (int)exactTime;
            if (time == previousTime)
                continue;
            strikes.Add(new TaikoStrike
            {
                Time = time,
                Keys = new[] { preferLeftHand ? TaikoKey.InnerLeft : TaikoKey.InnerRight },
                SourceKind = hitObject.Kind,
                SourceLine = hitObject.SourceLine,
                RequiredForCombo = false
            });
            preferLeftHand = !preferLeftHand;
            previousTime = time;
        }
    }

    private static void AddSpinnerStrikes(
        ICollection<TaikoStrike> strikes,
        TaikoBeatmapDocument beatmap,
        TaikoHitObject hitObject,
        int intervalOverride,
        TaikoGameplayModifiers modifiers)
    {
        var duration = checked((int)hitObject.EndTime - hitObject.StartTime);
        var hitCount = intervalOverride > 0
            ? Math.Max(1, (int)Math.Ceiling(duration / (double)intervalOverride))
            : CalculateNativeSpinnerRequiredHits(duration, beatmap.OverallDifficulty, modifiers) + 1;
        var interval = intervalOverride > 0
            ? intervalOverride
            : Math.Max(1, duration / hitCount);
        var index = 0;
        for (var strikeIndex = 0; strikeIndex < hitCount; strikeIndex++)
        {
            var time = checked(hitObject.StartTime + strikeIndex * interval);
            if (time >= hitObject.EndTime)
                break;
            strikes.Add(new TaikoStrike
            {
                Time = time,
                Keys = new[] { SpinnerCycle[index] },
                SourceKind = hitObject.Kind,
                SourceLine = hitObject.SourceLine,
                RequiredForCombo = false
            });
            index = (index + 1) % SpinnerCycle.Length;
        }
    }

    public static double ResolveNativeDrumRollInterval(
        TaikoBeatmapDocument beatmap,
        TaikoHitObject hitObject)
    {
        ArgumentNullException.ThrowIfNull(beatmap);
        ArgumentNullException.ThrowIfNull(hitObject);
        if (hitObject.Kind != TaikoObjectKind.DrumRoll)
            throw new ArgumentException("The hit object is not a drumroll.", nameof(hitObject));

        double interval;
        if (beatmap.FormatVersion < 8)
        {
            interval = (hitObject.BeatLength / hitObject.SliderVelocityMultiplier) / 8.0;
        }
        else
        {
            var unusualTickRate = beatmap.SliderTickRate is 1.5 or 3.0 or 6.0;
            interval = hitObject.BeatLength / (unusualTickRate ? 6.0 : 8.0);
        }
        while (interval < 60.0)
            interval *= 2.0;
        while (interval > 120.0)
            interval /= 2.0;
        return interval;
    }

    public static int CalculateNativeSpinnerRequiredHits(
        int durationMilliseconds,
        double overallDifficulty,
        TaikoGameplayModifiers modifiers = TaikoGameplayModifiers.None)
    {
        if (durationMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));
        ValidateModifiers(modifiers);

        var effectiveDifficulty = overallDifficulty;
        if ((modifiers & TaikoGameplayModifiers.Easy) != 0)
            effectiveDifficulty = Math.Max(0.0, effectiveDifficulty / 2.0);
        if ((modifiers & TaikoGameplayModifiers.HardRock) != 0)
            effectiveDifficulty = Math.Min(10.0, effectiveDifficulty * 1.4);

        var spinsPerSecond = DifficultyRange(effectiveDifficulty, 3.0, 5.0, 7.5);
        var baseRequired = (int)(((float)durationMilliseconds / 1000f) * spinsPerSecond);
        var required = (int)Math.Max(1f, baseRequired * 1.65f);
        if ((modifiers & TaikoGameplayModifiers.DoubleTime) != 0)
            required = Math.Max(1, (int)(required * 0.75f));
        if ((modifiers & TaikoGameplayModifiers.HalfTime) != 0)
            required = Math.Max(1, (int)(required * 1.5f));
        return required;
    }

    private static double DifficultyRange(double difficulty, double minimum, double middle, double maximum)
    {
        return difficulty > 5.0
            ? middle + (maximum - middle) * (difficulty - 5.0) / 5.0
            : middle - (middle - minimum) * (5.0 - difficulty) / 5.0;
    }

    private static List<TaikoKeyTransition> BuildTransitions(
        IReadOnlyList<TaikoStrike> strikes,
        int tapMilliseconds)
    {
        var downs = new Dictionary<TaikoKey, List<(int Time, TaikoObjectKind Kind, int SourceLine)>>();
        foreach (var key in Enum.GetValues<TaikoKey>())
            downs[key] = new List<(int, TaikoObjectKind, int)>();

        var seen = new HashSet<(int Time, TaikoKey Key)>();
        foreach (var strike in strikes)
        {
            foreach (var key in strike.Keys)
            {
                if (seen.Add((strike.Time, key)))
                    downs[key].Add((strike.Time, strike.SourceKind, strike.SourceLine));
            }
        }

        var transitions = new List<TaikoKeyTransition>(seen.Count * 2);
        foreach (var pair in downs)
        {
            var keyDowns = pair.Value;
            keyDowns.Sort((left, right) => left.Time.CompareTo(right.Time));
            for (var index = 0; index < keyDowns.Count; index++)
            {
                var down = keyDowns[index];
                var release = checked(down.Time + tapMilliseconds);
                if (index + 1 < keyDowns.Count && keyDowns[index + 1].Time <= release)
                    release = keyDowns[index + 1].Time - 1;
                if (release <= down.Time)
                    release = checked(down.Time + 1);

                transitions.Add(new TaikoKeyTransition
                {
                    Time = down.Time,
                    Key = pair.Key,
                    IsDown = true,
                    SourceKind = down.Kind,
                    SourceLine = down.SourceLine
                });
                transitions.Add(new TaikoKeyTransition
                {
                    Time = release,
                    Key = pair.Key,
                    IsDown = false,
                    SourceKind = down.Kind,
                    SourceLine = down.SourceLine
                });
            }
        }

        transitions.Sort((left, right) =>
        {
            var byTime = left.Time.CompareTo(right.Time);
            if (byTime != 0)
                return byTime;
            if (left.IsDown != right.IsDown)
                return left.IsDown ? 1 : -1;
            return left.Key.CompareTo(right.Key);
        });
        return transitions;
    }

    private static void ValidateTransitions(IReadOnlyList<TaikoKeyTransition> transitions)
    {
        var states = Enum.GetValues<TaikoKey>().ToDictionary(key => key, _ => false);
        var lastTime = int.MinValue;
        foreach (var transition in transitions)
        {
            if (transition.Time < lastTime)
                throw new InvalidOperationException("Taiko transition plan is not sorted.");
            if (states[transition.Key] == transition.IsDown)
            {
                throw new InvalidOperationException(
                    $"Illegal repeated {(transition.IsDown ? "down" : "up")} for {transition.Key} "
                    + $"at {transition.Time}ms (source line {transition.SourceLine}).");
            }
            states[transition.Key] = transition.IsDown;
            lastTime = transition.Time;
        }
        if (states.Any(pair => pair.Value))
            throw new InvalidOperationException("Taiko transition plan leaves one or more keys held.");
    }

    private static void ValidateRange(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, $"{name} must be from {minimum} through {maximum}.");
    }

    private static void ValidateIntervalOverride(int value, string name)
    {
        if (value != 0)
            ValidateRange(value, 10, 500, name);
    }

    private static void ValidateModifiers(TaikoGameplayModifiers modifiers)
    {
        if ((modifiers & TaikoGameplayModifiers.Easy) != 0
            && (modifiers & TaikoGameplayModifiers.HardRock) != 0)
        {
            throw new ArgumentException("Easy and HardRock cannot both be active.", nameof(modifiers));
        }
        if ((modifiers & TaikoGameplayModifiers.DoubleTime) != 0
            && (modifiers & TaikoGameplayModifiers.HalfTime) != 0)
        {
            throw new ArgumentException("DoubleTime and HalfTime cannot both be active.", nameof(modifiers));
        }
    }
}
