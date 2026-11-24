namespace Brutality.Euphoria
{
    /// <summary>
    /// Parameters of NaturalMotion Euphoria behaviors, with their official names and default values.
    /// Source: GTA V behaviours.xml (NaturalMotion data shipped with the game, common.rpf/data/naturalmotion),
    /// extracted and published by alexguirre/GTA-V-Euphoria-Behaviours-Parser (Rage.Euphoria classes from RAGE Plugin Hook).
    /// NaturalMotion's internal laws are not public: Brutality interprets the description of each parameter
    /// (see docs/EUPHORIA.md). A few Brutality-specific values are grouped in <see cref="NmTune"/>.
    ///
    /// Effector units: "stiffness" = natural angular frequency ω (rad/s, range 6..16), "damping" = damping
    /// ratio ζ (1 = critical, per configureSoftLimit), "muscleStiffness" = 1.01 − looseness
    /// (per shotFallToKnees.ftkImpactLooseness).
    /// </summary>
    internal static class NmShot
    {
        public const float BodyStiffness = 11f, SpineDamping = 1f, ArmStiffness = 10f;
        public const float InitialNeckStiffness = 14f, InitialNeckDamping = 1f, NeckStiffness = 14f, NeckDamping = 1f;
        public const float KMult4Legs = 0.3f, LoosenessAmount = 1f, Looseness4Fall = 0f, MinArmsLooseness = 0.1f, MinLegsLooseness = 0.1f;
        public const float GrabHoldTime = 2f, SpineBlendZero = 0.6f, TimeBeforeReachForWound = 0.2f;
        public const float ExagMag = 1f, ExagTwistMag = 0.5f;
        public const float CpainSmooth2Time = 0.2f, CpainMag = 1f, CpainTwistMag = 0.5f, CpainSmooth2Zero = 1.5f;
        public const float InitialWeaknessZeroDuration = 0f, InitialWeaknessRampDuration = 0.4f;
        public const float InitialNeckDuration = 0f, InitialNeckRampDuration = 0.4f;
    }

    internal static class NmBullets
    {
        public const float ImpulsePeriod = 0.1f, ImpulseTorqueScale = 1f, ImpulseMinLeakage = 0.2f;
        public const bool DoCounterImpulse = false;
        public const float CounterImpulseDelay = 0.03333f, CounterImpulseMag = 0.5f, CounterImpulse2Hips = 1f;
        public const float RbMoment = 1f, RbMaxTwistMomentArm = 0.5f, RbMaxBroomMomentArm = 1f;
        /// <summary>Head whip torque on a headshot (rad/s for a reference round).</summary>
        public const float HeadWhip = 14f;
    }

    internal static class NmBalance
    {
        public const float StepHeight = 0.1f, LegStiffness = 12f, BalanceAbortThreshold = 0.6f, GiveUpHeight = 0.5f;
        public const float StepClampScale = 1f, LegsApartRestep = 0.2f;
    }

    internal static class NmStagger
    {
        public const float ArmStiffness = 12f, SpineStiffness = 10f, LowerBodyStiffness = 13f, LowerBodyStiffnessEnd = 8f;
        public const int StepsTillStartEnd = 2;
        public const float PredictionTime = 0.1f, LeanInDirRate = 1f, LeanInDirMaxF = 0.1f, LeanInDirMaxB = 0.3f, SpineBendMult = 0.4f;
    }

    internal static class NmStayUpright
    {
        public const float ForceStrength = 3f, LastStandSinkRate = 0.3f, LastStandHorizDamping = 0.4f, LastStandMaxTime = 0.4f;
    }

    internal static class NmFallToKnees
    {
        public const float FtkBalanceTime = 0.7f, FtkHelperForce = 200f, FtkBendRate = 0.7f, FtkImpactLooseness = 0.5f;
    }

    internal static class NmShotFromBehind
    {
        public const float SfbSpineAmount = 4f, SfbNeckAmount = 1f, SfbKneeAmount = 0.05f, SfbPeriod = 0.7f, SfbForceBalancePeriod = 0.3f;
    }

    internal static class NmShotInGuts
    {
        public const float SigSpineAmount = 2f, SigNeckAmount = 1f, SigHipAmount = 1f, SigPeriod = 2f;
    }

    internal static class NmShotSnap
    {
        public const float SnapMag = 0.4f, SnapDirectionRandomness = 0.3f, UnSnapInterval = 0.01f, UnSnapRatio = 0.7f;
    }

    internal static class NmShockSpin
    {
        public const float ShockSpinMin = 50f, ShockSpinMax = 90f, ShockSpinDecayMult = 4f, ShockSpinScalePerComponent = 0.5f;
    }

    internal static class NmHeadLook
    {
        public const float AtWoundMin = 0.25f, AtWoundMax = 0.8f, AtHeadPosMin = 0.6f, AtHeadPosMax = 1.7f, Stiffness = 10f;
    }

    internal static class NmFling2
    {
        public const float TimeBefore = 0.1f, Time = 0.5f, MStiff = 0.95f, AngleMin = -1.5f, AngleMax = 1f, LengthMin = 0.25f, LengthMax = 0.6f;
    }

    internal static class NmInjured
    {
        public const float InjuredArmTime = 0.25f, HipYaw = 0.8f;
        public const float TimeBeforeCollapseWoundLeg = 0.3f, LegInjury = 0.3f;
    }

    internal static class NmWindmill
    {
        public const float Radius1 = 0.75f, Speed = 1f, ShoulderStiffness = 12f, ElbowStiffness = 12f, ElbowMin = 0.5f, AngVelGain = 1f;
    }

    internal static class NmCatchFall
    {
        public const float TorsoStiffness = 9f, LegsStiffness = 6f, ArmsStiffness = 15f;
        public const float BackwardsMinArmOffset = -0.25f, ForwardMaxArmOffset = 0.35f, ExtraSit = 1f;
        public const float ArmReduceSpeed = 2.5f, ComVelRdsThresh = 2f;   // setFallingReaction
    }

    internal static class NmRollDownStairs
    {
        public const float Stiffness = 11f, Damping = 1.4f, ForceMag = 0.55f, ArmReachAmount = 1.4f, LegPush = 1f;
        public const float TargetLinearVelocity = 1f, TargetLinearVelocityDecayTime = 0.5f;
    }

    internal static class NmWrithe
    {
        public const float ArmStiffness = 13f, BackStiffness = 13f, LegStiffness = 13f;
        public const float ArmDamping = 0.5f, BackDamping = 0.5f, LegDamping = 0.5f;
        public const float ArmPeriod = 1f, BackPeriod = 1f, LegPeriod = 1f;
        public const float ArmAmplitude = 1f, BackAmplitude = 1f, LegAmplitude = 1f, ElbowAmplitude = 1f, KneeAmplitude = 1f;
    }

    internal static class NmPedal
    {
        public const float Radius = 0.25f, AngularSpeed = 10f, LegStiffness = 10f, SpeedAsymmetry = 8f, RadiusVariance = 0.4f;
    }

    internal static class NmFoetal
    {
        public const float Stiffness = 9f, DampingFactor = 1.4f;
    }

    internal static class NmCharacterDamping
    {
        // setCharacterDamping: default thresholds, "try 0.45 / try 0.8" damping values from the documentation.
        public const float SomersaultThresh = 34f, SomersaultDamp = 0.45f, CartwheelThresh = 27f, CartwheelDamp = 0.8f;
    }

    internal static class NmCollisions
    {
        // balancerCollisionsReaction
        public const int NumStepsTillSlump = 4;
        public const float ImpactLegStiffReduction = 3f, SlumpLegStiffRate = 8f;
    }

    /// <summary>
    /// Brutality-specific settings: GTA's shot presets (physicstasks) are not public.
    /// </summary>
    internal static class NmTune
    {
        public const float ExagDuration = 0.22f, ExagSmooth2Zero = 0.3f, CpainDuration = 0.25f;
        public const float SpineLeanUnit = 1f;      // rad per unit of "Lean magnitude"
        public const float AmountUnitSpine = 0.1f;  // rad per unit of sfbSpineAmount
        public const float AmountUnitNeck = 0.25f, AmountUnitHip = 0.22f, KneeAmountUnit = 4f;
        public const float ImpulseReductionPerShot = 0.25f, ImpulseRecovery = 0.5f;   // NaturalMotion default: 0 / 0
    }
}
