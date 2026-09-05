// The page-to-worker wire format, written once and shared by both ends.
//
// ★★ This is the reason the playground is TypeScript at all. The two halves talk over
// `postMessage`, which is `any` in both directions, and the protocol was previously spelled out
// only in the shape of the object literals at each end — the page building `{ id, kind, source }`
// and the worker destructuring it. Nothing connected the two. The specific hazard: `kind` is
// selected in the worker by a chain of ternaries whose final branch is `Run`, so a misspelled kind
// did not fail, it RAN THE PROGRAM.
//
// ⚠ Both ends must import from here. `worker.js` is JavaScript (the .NET SDK names it in
// WasmMainJSPath and copies it verbatim), so it reaches these through JSDoc `@typedef` imports
// under `// @ts-check` — the same types, checked the same way.

/** Which of the runtime's three entry points a request wants. */
export type RequestKind = 'run' | 'check' | 'tokens';

/** A question for the runtime. `id` comes back on the answer, and is how the two are paired. */
export interface RuntimeRequest {
    id: number;
    kind: RequestKind;
    source: string;
}

/** The runtime answered. `result` is whatever that entry point returns, always a string. */
export interface RuntimeSuccess {
    id: number;
    ok: true;
    result: string;
    elapsed: number;
}

/**
 * The runtime itself failed. Not a Cufet-level error — `Run` and `Check` turn those into ordinary
 * return values, so anything arriving here is the host failing and is worth showing.
 */
export interface RuntimeFailure {
    id: number;
    ok: false;
    error: string;

    /**
     * The runtime EXITED and will answer nothing further — the page has to replace the worker.
     *
     * ⚠⚠ Measured: `examples/algorithms/sudoku.cufe` exhausts the browser stack, and after that
     * every later run throws too. Without this the page reported a failure, left `booted` true, and
     * went on talking to a corpse — one deep program bricked the playground until a reload.
     */
    fatal: boolean;
}

/**
 * Sent once, unprompted, when the worker has finished booting. It carries no `id` because it
 * answers no question — which is exactly how the page tells it apart from a reply.
 */
export interface RuntimeReady {
    ready: true;
}

export type RuntimeAnswer = RuntimeSuccess | RuntimeFailure | RuntimeReady;

/** Narrows the unprompted boot notice out of the answers that pair with a request. */
export function isReady(answer: RuntimeAnswer): answer is RuntimeReady {
    return 'ready' in answer;
}
