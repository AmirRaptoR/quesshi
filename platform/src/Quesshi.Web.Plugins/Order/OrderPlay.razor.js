export function capture(element, pointerId) {
    element.setPointerCapture(pointerId);
}

export function release(element, pointerId) {
    if (element.hasPointerCapture(pointerId)) {
        element.releasePointerCapture(pointerId);
    }
}
