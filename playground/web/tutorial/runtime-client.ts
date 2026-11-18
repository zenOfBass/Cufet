// The tutorial's handle on the Cufet runtime, which lives in worker.js exactly as the playground's
// does. What the tutorial needs that the playground does not: one call that says what HAPPENED
// (ran, refused, ran too long), and a time limit it enforces itself.
//
// ★ The time limit is the answer to lesson 6's open question, "a loop that never ends". The
// interpreter has no limit of its own and cannot be interrupted from outside a synchronous run (see
// worker.js), so a run that goes on too long is ended the way the playground's Stop ends one: the
// worker is killed and a fresh one started. Every run builds a fresh interpreter anyway, so nothing
// is lost but the run.

import { isReady } from '../protocol';
import type { RequestKind, RuntimeAnswer, RuntimeFailure, RuntimeSuccess } from '../protocol';
import type { Attempt } from './judge';

/** How long a player's program may run. A tutorial program finishes in milliseconds. */
export const TIME_LIMIT_MS = 5000;

type Reply = RuntimeSuccess | RuntimeFailure;
type Waiting = { resolve: (answer: Reply | null) => void };

export class RuntimeClient {
    private worker: Worker | null = null;
    private booted = false;
    private nextId = 1;
    private waiting = new Map<number, Waiting>();
    private readyWaiters: (() => void)[] = [];

    constructor(private readonly onReadyChange: (ready: boolean) => void = () => { }) {
        this.spawn();
    }

    get ready(): boolean { return this.booted; }

    private spawn(): void {
        this.booted = false;
        this.onReadyChange(false);
        const worker = new Worker('./worker.js', { type: 'module' });
        worker.onmessage = ({ data }: MessageEvent<RuntimeAnswer>) => {
            if (worker !== this.worker) return;      // a reply from a worker already replaced
            if (isReady(data)) {
                this.booted = true;
                this.onReadyChange(true);
                for (const wake of this.readyWaiters.splice(0)) wake();
                return;
            }
            const waiter = this.waiting.get(data.id);
            if (!waiter) return;
            this.waiting.delete(data.id);
            waiter.resolve(data);
        };
        this.worker = worker;
    }

    /** Kill the worker and start another; everything waiting on the old one gets null. */
    private replace(): void {
        this.worker?.terminate();
        for (const waiter of this.waiting.values()) waiter.resolve(null);
        this.waiting.clear();
        this.spawn();
    }

    private whenReady(): Promise<void> {
        return this.booted ? Promise.resolve() : new Promise(wake => this.readyWaiters.push(wake));
    }

    /** One request. Null when the worker was replaced before it answered. */
    private request(kind: RequestKind, source: string, limitMs?: number): Promise<Reply | null> {
        const worker = this.worker;
        if (!worker) return Promise.resolve(null);
        const id = this.nextId++;
        return new Promise(resolve => {
            let timer: ReturnType<typeof setTimeout> | undefined;
            this.waiting.set(id, {
                resolve: answer => { clearTimeout(timer); resolve(answer); },
            });
            if (limitMs !== undefined)
                timer = setTimeout(() => {
                    if (!this.waiting.has(id)) return;
                    this.waiting.delete(id);
                    resolve(null);
                    this.replace();
                }, limitMs);
            worker.postMessage({ id, kind, source });
        });
    }

    /**
     * Ask a question that is not a run (`check`, `tokens`). Answers null rather than waiting when
     * the runtime is not up, so the editor's colouring never queues behind a boot.
     */
    async ask(kind: RequestKind, source: string): Promise<string | null> {
        if (!this.booted) return null;
        const answer = await this.request(kind, source);
        return answer && answer.ok ? answer.result : null;
    }

    /**
     * Run `code` the way the player's Run button does, and say what happened.
     *
     * ★ Refused is decided by the CHECKER's own report (`check`, the same front end the editor
     * extension calls), not by reading the run's text: a program that runs and prints a sentence
     * that happens to look like an error is still a program that ran. The text shown is still the
     * run's, so the player sees exactly the message the command line would print.
     */
    async attempt(code: string): Promise<Attempt> {
        if (code.trim() === '') return { code, outcome: 'empty', output: '' };
        await this.whenReady();

        const report = await this.request('check', code);
        const refused = report !== null && report.ok
            && report.result.split('\n').some(line => {
                try { return (JSON.parse(line) as { severity?: string }).severity === 'error'; }
                catch { return false; }
            });

        const answer = await this.request('run', code, TIME_LIMIT_MS);
        if (answer === null) return { code, outcome: 'timeout', output: '' };
        if (!answer.ok) {
            if (answer.fatal) this.replace();
            return { code, outcome: 'died', output: answer.error };
        }
        return { code, outcome: refused ? 'refused' : 'ran', output: answer.result };
    }
}
