namespace PCLCS.Server;

public sealed class DeploymentControl {
    private readonly object gate = new();
    private bool requested, paused, finished;
    public bool PauseRequested { get { lock (gate) return requested; } }
    public bool IsPaused { get { lock (gate) return paused; } }
    public bool IsFinished { get { lock (gate) return finished; } }
    public void Pause() { lock (gate) { if (!finished) requested = true; } }
    public void Resume() { lock (gate) { requested = false; paused = false; Monitor.PulseAll(gate); } }
    public void Acknowledge(bool value) { lock (gate) paused = value && requested && !finished; }
    public void Checkpoint() {
        lock (gate) {
            while (requested && !finished) { paused = true; Monitor.Wait(gate); }
            paused = false;
        }
    }
    public void Finish() { lock (gate) { finished = true; requested = false; paused = false; Monitor.PulseAll(gate); } }
}
