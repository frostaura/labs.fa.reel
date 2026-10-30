// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { cardUndoCopy, focusMayReturn, focusReturnTarget, type CardAction } from "./cardUndo";

const base = { titleId: "t1", mediaType: "movie", tmdbId: 7, name: "Heat" };

describe("cardUndoCopy", () => {
  it("says what happened to the title, per action", () => {
    expect(cardUndoCopy({ kind: "save", ...base }).message).toBe("Saved “Heat” to your queue.");
    expect(cardUndoCopy({ kind: "dismiss", ...base }).message).toBe("“Heat” won’t be picked for you again.");
    expect(cardUndoCopy({ kind: "rate", ...base, rating: 8 }).message).toBe("Rated “Heat” 8 — logged as seen.");
    expect(cardUndoCopy({ kind: "save", ...base }).undoLabel).toBe("Undo");
  });

  it("says a failed undo changed nothing and offers to try again", () => {
    const failed = (action: CardAction) => cardUndoCopy(action, true);
    expect(failed({ kind: "save", ...base })).toEqual({ message: "Couldn’t undo — “Heat” is still saved.", undoLabel: "Try again" });
    expect(failed({ kind: "dismiss", ...base }).message).toBe("Couldn’t undo — “Heat” is still hidden from your picks.");
    expect(failed({ kind: "rate", ...base, rating: 3 }).message).toBe("Couldn’t undo — Your 3 for “Heat” still stands.");
  });
});

/** A page of rails: each rail a <section> of cards, each card a title link plus its action button. */
function page(rails: string[][], { main = true } = {}): HTMLElement {
  document.body.innerHTML = "";
  const root = document.createElement(main ? "main" : "div");
  for (const [i, names] of rails.entries()) {
    const section = document.createElement("section");
    section.id = `rail-${i}`;
    for (const name of names) {
      const card = document.createElement("div");
      card.dataset.testid = i === 0 && rails.length > 1 ? "hero-card" : "rec-card";
      card.innerHTML = `<a href="/t/${name}" id="link-${name}">${name}</a><button id="act-${name}">x</button>`;
      section.append(card);
    }
    root.append(section);
  }
  document.body.append(root);
  return root;
}
const act = (name: string) => document.getElementById(`act-${name}`)!;
const remove = (...names: string[]) => names.forEach((n) => document.getElementById(`link-${n}`)!.parentElement!.remove());

describe("focusReturnTarget (HREV4-PH-02)", () => {
  it("goes to the next card's title link, in the same rail", () => {
    page([["a", "b", "c"]]);
    const returnTo = focusReturnTarget(act("a"));
    remove("a");
    expect(returnTo()?.id).toBe("link-b");
  });

  it("goes to the previous card when the acted-on card was the rail's last", () => {
    page([["a", "b", "c"]]);
    const returnTo = focusReturnTarget(act("c"));
    remove("c");
    expect(returnTo()?.id).toBe("link-b");
  });

  it("skips a neighbour that has left since the action started", () => {
    page([["a", "b", "c"]]);
    const returnTo = focusReturnTarget(act("a"));
    remove("a", "b");
    expect(returnTo()?.id).toBe("link-c");
  });

  it("never returns the acted-on card itself, even while it is still on the page", () => {
    page([["a", "b"]]);
    expect(focusReturnTarget(act("a"))()?.id).toBe("link-b");
  });

  it("goes on to the next rail's first card when the rail leaves with its last card (the hero)", () => {
    page([["hero"], ["p", "q"]]);
    const returnTo = focusReturnTarget(act("hero"));
    document.getElementById("rail-0")!.remove();
    expect(returnTo()?.id).toBe("link-p");
  });

  it("goes back to the nearest earlier card when nothing follows the rail", () => {
    page([["hero"], ["p", "q"], ["z"]]);
    const returnTo = focusReturnTarget(act("z"));
    document.getElementById("rail-2")!.remove();
    expect(returnTo()?.id).toBe("link-q");
  });

  it("falls back to the rail, then to <main>, each focusable without joining the Tab order", () => {
    page([["only"]]);
    const returnTo = focusReturnTarget(act("only"));
    remove("only");
    const rail = returnTo();
    expect(rail?.id).toBe("rail-0");
    expect(rail?.getAttribute("tabindex")).toBe("-1");
    rail!.remove();
    const main = returnTo();
    expect(main?.tagName).toBe("MAIN");
    expect(main?.getAttribute("tabindex")).toBe("-1");
  });

  it("keeps a region's own tabindex, and gives nothing at all when no region is left", () => {
    const root = page([["only"]], { main: false });
    document.getElementById("rail-0")!.setAttribute("tabindex", "0");
    const returnTo = focusReturnTarget(act("only"));
    remove("only");
    expect(returnTo()?.getAttribute("tabindex")).toBe("0");
    root.remove();
    expect(returnTo()).toBeNull();
  });
});

describe("focusMayReturn", () => {
  it("hands focus back only from the toast or from <body>, never from wherever someone tabbed on to", () => {
    document.body.innerHTML = `<div id="toast"><button id="undo">Undo</button></div><button id="elsewhere">x</button>`;
    const toast = document.getElementById("toast");
    expect(focusMayReturn(document.getElementById("undo"), toast)).toBe(true);
    expect(focusMayReturn(document.body, toast)).toBe(true);
    expect(focusMayReturn(null, toast)).toBe(true);
    expect(focusMayReturn(document.getElementById("elsewhere"), toast)).toBe(false);
    expect(focusMayReturn(document.getElementById("elsewhere"), null)).toBe(false);
  });
});
