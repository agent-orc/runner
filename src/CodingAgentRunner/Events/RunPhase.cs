namespace CodingAgentRunner.Events;

/// <summary>
/// Lifecycle phase a CLI run is in. Drives a phase-aware watchdog: "no
/// <see cref="SessionInitializing"/> &#8594; <see cref="PromptConsumed"/>
/// transition within a few seconds of <see cref="Spawning"/>" is a different
/// failure mode than "no <see cref="OutputDelta"/> within the silence window of
/// <see cref="TurnInProgress"/>", and a consumer can surface them differently.
/// Each adapter advances the phase as it observes the CLI's typed events.
///
/// <para>
/// Order matters: phases progress monotonically per turn (with the exception of
/// <see cref="OutputDelta"/>, which is a sticky state inside
/// <see cref="TurnInProgress"/>) until the run terminates. Going backwards is a
/// programming error; <see cref="RunPhaseTransitions"/> clamps it rather than
/// crashing.
/// </para>
/// </summary>
public enum RunPhase
{
    /// <summary>Process started but no event yet.</summary>
    Spawning,
    /// <summary>First adapter event seen but the CLI's session/init handshake is not complete.</summary>
    SessionInitializing,
    /// <summary>Session/init complete; the prompt has been delivered to the model.</summary>
    PromptConsumed,
    /// <summary>The model is processing a turn; we expect output deltas, tool calls, or completion next.</summary>
    TurnInProgress,
    /// <summary>The model is producing visible output (tokens / text deltas).</summary>
    OutputDelta,
    /// <summary>A tool is executing on behalf of the agent. Often longer-running than ordinary turns.</summary>
    ToolExecuting,
    /// <summary>The current turn ended successfully.</summary>
    TurnCompleted,
    /// <summary>The current turn ended with an error.</summary>
    TurnFailed,
    /// <summary>The agent is asking the user for input (a needs-input / approval / clarification signal).</summary>
    NeedsInput,
    /// <summary>The CLI process has exited cleanly.</summary>
    Exited,
    /// <summary>The runner killed the process tree.</summary>
    Killed,
    /// <summary>Adapter could not classify the CLI's output. The run continues; the watchdog notes this and surfaces a meta line.</summary>
    Unknown
}
