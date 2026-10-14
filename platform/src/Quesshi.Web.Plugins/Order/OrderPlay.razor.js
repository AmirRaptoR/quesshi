export function capture(element, pointerId) {
    element.setPointerCapture(pointerId);
}

export function midpoints(element) {
    return Array.from(element.querySelectorAll('.sortrow'), row => {
        const { top, height } = row.getBoundingClientRect();
        return top + height / 2;
    });
}

export function release(element, pointerId) {
    if (element.hasPointerCapture(pointerId)) {
        element.releasePointerCapture(pointerId);
    }
}
