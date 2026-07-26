# Multiplayer Undo/Redo Manual Test Matrix

Run every scenario twice:

1. The host plays Blank.
2. A client plays Blank.

## Ownership

- Player A draws one stroke; Player A presses Undo. Only A's stroke disappears.
- Player A draws, then Player B draws; Player A presses Undo. B's stroke remains.
- Player A has no history and presses Undo. The canvas does not change.
- Each player draws at least two operations and alternates Undo. Each request removes only that player's latest operation.

## Overlap

- Player A draws a red stroke; Player B covers it with blue; Player B undoes. The red stroke is revealed.
- Player A draws a red stroke; Player B covers it with blue; Player A undoes. The blue stroke remains.
- Player A undoes and redoes a red stroke covered by Player B's later blue stroke. The blue stroke remains on top.
- Both players draw crossing strokes at the same time, release in different orders, and compare both screens after each release.

## Redo ownership and branching

- Player A undoes twice, then presses Ctrl+Y twice. A's two operations return in undo-reverse order.
- Repeat with Ctrl+Shift+Z. It behaves exactly like Ctrl+Y.
- Player A undoes, then Player B draws. Player A can still redo.
- Player A undoes, then Player A draws a new operation. Player A can no longer redo the old operation.
- Player A has no redo history and presses Ctrl+Y. The canvas does not change.
- Players A and B each undo, then alternate Ctrl+Y. Each request restores only that player's own operation.

## Stateful tools

- Player A draws a closed boundary; Player B fills inside it; Player A undoes the boundary. The fill keeps its committed pixel mask.
- Player A places a stamp; Player B draws over it; Player A undoes. Player B's later stroke remains.
- Player A clears the canvas; Player A undoes. Earlier committed operations reappear.

## Race boundaries

- Player A holds the mouse and keeps drawing while Player B requests Undo. The partial in-flight stroke is cancelled on every client.
- A client requests Undo while command batches are still arriving. Old-epoch commands must not reappear after the authoritative canvas state.
- Spam Ctrl+Z on two clients at the same time. Every accepted undo must advance the same canvas state on all clients.
- Spam Ctrl+Y on two clients at the same time. Every accepted redo must advance the same canvas state on all clients.
- Alternate Ctrl+Z and Ctrl+Y rapidly while the other player starts a stroke. In-flight strokes are cancelled and never reappear.
- Draw immediately after receiving an undo state. The new stroke must use the new epoch and remain visible.

## Limits and lifecycle

- Draw 21 operations with one player. Only the latest 20 are undoable.
- Confirm while another player has an unfinished stroke. The final PNG must be identical on all clients.
- End combat while the drawing screen is open. The drawing UI and pending history must close cleanly.
- Abandon the run while drawing. No drawing or undo UI may remain.
- Use the Infinite Gallery drawing screen and repeat ownership and overlap tests.

## Evidence

For any failure, collect `godot.log` from every player and note:

- Which player played Blank.
- Which player requested Undo.
- Whether Ctrl+Z, Ctrl+Y, or Ctrl+Shift+Z was used.
- Operation order and tool types.
- Whether the host or a client saw the incorrect canvas first.
