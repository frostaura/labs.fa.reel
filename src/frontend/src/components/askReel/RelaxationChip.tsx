import { useEffect, useRef, useState } from "react";
import { SlidersHorizontal } from "lucide-react";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle, DialogTrigger } from "../ui/dialog";
import ConditionsEditor from "../settings/ConditionsEditor";
import PillButton from "./PillButton";
import { CONDITIONS_SCOPE_COPY } from "../../lib/conditionsCopy";
import { EDITOR, relaxationLabel, ruleTarget } from "../../lib/askReel/relaxation";
import type { AskKnockout } from "../../lib/askReelStream";

/** What can take focus without a tab stop of its own being invented. */
const FOCUSABLE = 'button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), a[href], [tabindex]:not([tabindex="-1"])';

/**
 * Where focus lands for a rule's control. A control that is itself focusable takes it; a container
 * (the genre chip is a `role="group"` around its include and exclude buttons) hands it to its first
 * focusable child — focusing the bare group did nothing, so the reader was left on the dialog's
 * close button; anything else (the editor's root) is made focusable by script only.
 */
function focusTarget(el: HTMLElement): HTMLElement {
  if (el.matches(FOCUSABLE)) return el;
  const inner = el.querySelector<HTMLElement>(FOCUSABLE);
  if (inner) return inner;
  el.setAttribute("tabindex", "-1");
  return el;
}

/** How long the dialog keeps looking for the rule's control while the editor loads its filters. */
const FIND_BUDGET_MS = 3_000;
const FIND_STEP_MS = 100;

/**
 * One relaxation a none-found answer offers — "Without “English only”: +4" — as a control that opens
 * the Conditions editor ON that rule (WS-2 task 3; FD-2c's default: no "just this once" override,
 * the chip takes you to the rule). It used to be an inert `<li>` naming a number nobody could act on
 * from where they read it.
 *
 * The same editor the feed's Conditions button mounts — one implementation of these rules — with
 * one addition: once it has rendered, the control the rule lives in is scrolled into view and
 * focused, so the reader lands on the genre chip or the language allow-list rather than on the top
 * of a long form. The editor loads its own filters, so the control is looked for until it exists
 * (bounded); a rule with no control of its own lands on the editor.
 */
export default function RelaxationChip({ knockout }: { knockout: AskKnockout }) {
  const [open, setOpen] = useState(false);
  const contentRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const target = ruleTarget(knockout);
    const started = Date.now();
    let timer: ReturnType<typeof setTimeout> | null = null;
    const look = () => {
      const root = contentRef.current;
      const el = root?.querySelector<HTMLElement>(target) ?? (Date.now() - started >= FIND_BUDGET_MS ? root?.querySelector<HTMLElement>(EDITOR) : null);
      if (el) {
        el.scrollIntoView({ block: "center" });
        const landing = focusTarget(el);
        landing.focus({ preventScroll: true });
        el.setAttribute("data-askreel-rule", "focused");
        return;
      }
      if (Date.now() - started < FIND_BUDGET_MS + FIND_STEP_MS) timer = setTimeout(look, FIND_STEP_MS);
    };
    timer = setTimeout(look, 0);
    return () => {
      if (timer !== null) clearTimeout(timer);
    };
  }, [open, knockout]);

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger asChild>
        <PillButton tone="accent" data-testid="askreel-relax" className="whitespace-normal py-2 text-left">
          <SlidersHorizontal className="h-3.5 w-3.5 shrink-0" aria-hidden />
          {relaxationLabel(knockout)}
        </PillButton>
      </DialogTrigger>
      <DialogContent ref={contentRef} className="max-w-2xl max-h-[85vh] overflow-y-auto p-0">
        <DialogHeader>
          <DialogTitle>Conditions for tonight&apos;s picks</DialogTitle>
          <DialogDescription data-testid="askreel-relax-scope">
            {`Turning off “${knockout.label}” would unlock ${knockout.wouldUnlock}. ${CONDITIONS_SCOPE_COPY}`}
          </DialogDescription>
        </DialogHeader>
        <div className="px-6 pb-6">
          <ConditionsEditor
            bare
            saveLabel="Save conditions"
            testIds={{ editor: "conditions-editor", locked: "conditions-locked", loading: "conditions-loading" }}
            onSaved={() => setOpen(false)}
          />
        </div>
      </DialogContent>
    </Dialog>
  );
}
