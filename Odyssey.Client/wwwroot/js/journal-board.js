// After a keyboard-driven board move re-renders the kanban, return focus to the moved card's move
// cluster so the keyboard user keeps their place (issue #315 review). Focuses the first enabled move
// button in the card, or the card itself as a fallback.
export function focusMove(cardId) {
    const card = document.getElementById(`odc-board-card-${cardId}`);
    if (!card) {
        return;
    }
    const btn = card.querySelector('.odc-board-move:not([disabled])');
    (btn ?? card).focus();
}
