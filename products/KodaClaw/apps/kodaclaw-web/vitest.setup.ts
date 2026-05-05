import "@testing-library/jest-dom/vitest";
import { vi } from "vitest";

// jsdom does not implement HTMLDialogElement.showModal / close.
// Polyfill them so Modal.tsx effects don't throw in unit tests.
HTMLDialogElement.prototype.showModal = function () {
  this.setAttribute("open", "");
};
HTMLDialogElement.prototype.close = function () {
  this.removeAttribute("open");
};

// jsdom does not implement scrollIntoView — used by MessageTimeline and other components.
Element.prototype.scrollIntoView = vi.fn();
