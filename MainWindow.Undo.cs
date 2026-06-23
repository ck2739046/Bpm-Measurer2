using System.Linq;
using System.Windows.Input;
using StateManagement;

namespace BpmMeasurer;

/// <summary>
/// Undo/Redo wiring for discrete timing edits only: the global offset, the
/// per-segment beat index / bpm / beats-per-bar edited via StepperInput, and
/// segment add/remove. Drags (offset / BPM / seek), wheel zoom/pan bypass these
/// helpers and are never recorded. Audio load and config import reset the stack
/// (those bulk replaces are not undoable). Backed by the UndoService memento stack.
///
/// Recording happens at the *call sites* (the StepperInput lambdas and the
/// add/remove button handlers), never inside UpdateRaw* / RemoveRawPoint — those
/// helpers are shared with the BPM drag path (OverlayCanvas_MouseMove) which must
/// stay unrecorded. RecordTimingIfChanged compares against the last snapshot so
/// rejected edits (duplicate beat index, unchanged value) produce no entry.
/// </summary>
public partial class MainWindow
{
    private const int UndoCapacity = 1000;

    private UndoService<TimingSnapshot>? _undo;
    private TimingSnapshot _lastRecorded;
    private bool _applyingUndo;

    // ── Snapshot capture / restore ──

    private TimingSnapshot CaptureTiming()
        => new(_globalOffset, _rawPoints.ToArray());

    private void GetTimingState(out TimingSnapshot state)
        => state = CaptureTiming();

    private void SetTimingState(TimingSnapshot state)
    {
        // Defensive copy on restore: never alias the snapshot's array back into _rawPoints,
        // otherwise later edits would mutate the stored snapshot too.
        _globalOffset = state.Offset;
        _rawPoints = state.Points.ToList();
        RefreshTimingPoints();
    }

    /// <summary>
    /// Called at the end of each discrete edit. Records a new undo entry only when
    /// the state actually changed, so rejected edits (e.g. a duplicate beat index
    /// that UpdateRawBeatIndex rejects) or no-op stepper commits produce no entry.
    /// </summary>
    private void RecordTimingIfChanged()
    {
        if (_undo == null || _applyingUndo) return;
        var current = CaptureTiming();
        if (current.Equals(_lastRecorded))
            return;
        _undo.RecordState(null);
        _lastRecorded = current;
    }

    /// <summary>
    /// Constructs the undo service and records the initial baseline. Called once
    /// from the MainWindow constructor after the timing state is first set up.
    /// </summary>
    private void InitUndo()
    {
        _undo = new UndoService<TimingSnapshot>(GetTimingState, SetTimingState, UndoCapacity);
        _lastRecorded = CaptureTiming();
    }

    /// <summary>
    /// Clears the undo/redo history and re-baselines on the current state. Used
    /// after audio load and config import so those bulk replaces are not undoable.
    /// </summary>
    private void ResetUndoHistory()
    {
        if (_undo == null) { InitUndo(); return; }
        _undo.Reset();
        _lastRecorded = CaptureTiming();
    }

    // ── Commands for InputBindings (global Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z) ──

    private static readonly RoutedUICommand UndoCommand = new(
        "Undo", "Undo", typeof(MainWindow),
        new InputGestureCollection { new KeyGesture(Key.Z, ModifierKeys.Control) });

    private static readonly RoutedUICommand RedoCommand = new(
        "Redo", "Redo", typeof(MainWindow),
        new InputGestureCollection {
            new KeyGesture(Key.Y, ModifierKeys.Control),
            new KeyGesture(Key.Z, ModifierKeys.Control | ModifierKeys.Shift)
        });

    private void UndoCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        // When focus is in a text box (e.g. typing a BPM value that is not yet committed),
        // defer to the TextBox's native undo instead of performing a timing undo. Once the
        // edit is committed focus leaves the text box, and Ctrl+Z then reverts the timing.
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)
            return;
        if (_undo == null || !_undo.CanUndo)
            return;
        PerformUndoRedo(redo: false);
        e.Handled = true;
    }

    private void RedoCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)
            return;
        if (_undo == null || !_undo.CanRedo)
            return;
        PerformUndoRedo(redo: true);
        e.Handled = true;
    }

    private void PerformUndoRedo(bool redo)
    {
        if (_isPlaying) PausePlayback();
        // Snapshot the raw data by id so we can detect which segment changed.
        var oldById = _rawPoints.ToDictionary(p => p.Id);
        _applyingUndo = true;
        try
        {
            if (redo) _undo!.Redo();
            else _undo!.Undo();
        }
        finally
        {
            _applyingUndo = false;
        }
        // Re-sync the dedup mirror so a subsequent edit that returns to this value
        // is still recorded, and undo-then-immediately-redo of a no-op isn't skipped.
        _lastRecorded = CaptureTiming();

        // Auto-expand the segment whose value actually changed (undo/redo may have
        // touched any combination of offset / beat / bpm / beats-per-bar).
        foreach (var kv in oldById)
        {
            var match = _rawPoints.Find(p => p.Id == kv.Key);
            if (match.Equals(default)) continue;
            if (!kv.Value.Equals(match))
            {
                _expandedSegmentId = kv.Key;
                RebuildSegmentList();
                break;
            }
        }

        // Keep keyboard focus on a valid Focusable anchor so the next keyboard
        // route stays valid.
        if (OverlayCanvas.IsVisible)
            Keyboard.Focus(OverlayCanvas);
        else
            Keyboard.Focus(this);
    }
}
