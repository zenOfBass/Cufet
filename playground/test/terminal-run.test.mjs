// `run … with the terminal` in a browser, where there is no terminal and no processes at all.
//
// ★★ The refusal has to be the CUFET one. A program launch under wasm throws .NET's
// PlatformNotSupportedException, and left uncaught it once reached a visitor as
// `Process_PlatformNotSupported` — a resource key, in a language whose whole argument is that its
// messages are readable. That was found by running the corpus through the playground and fixed by
// routing it to CannotRunPrograms. This pins the new form to the same path, so it cannot regress
// through a door the original fix did not know about.
//
// ⚠ The form is only ever REFUSED here, which is why one test covers it. There is no terminal to
// hand a child, and no child to hand it to.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const site = join(here, '..', 'site');

const program = [
    'Try to:',
    '    Define outcome as run "sh" with the terminal with arguments ("-c", "exit 3").',
    '    State the exit-code of outcome.',
    'Done.',
    'In case of failure:',
    '    State "launch-failed".',
    'Done.',
].join('\n');

test('run with the terminal is refused in the browser, in Cufet\'s own words', () => {
    assert.ok(existsSync(join(site, '_framework')),
        'the playground is not built — run `dotnet publish Cufet.Playground.csproj -c Release` then `node build.mjs`');

    const script = `
        import { dotnet } from ${JSON.stringify(pathToFileURL(join(site, '_framework', 'dotnet.js')).href)};
        const { getAssemblyExports, getConfig } = await dotnet.create();
        const ex = await getAssemblyExports(getConfig().mainAssemblyName);
        const src = ${JSON.stringify(program)};
        const answer = { check: ex.Cufet.Playground.Runtime.Check(src) };
        try { answer.ran = ex.Cufet.Playground.Runtime.Run(src); }
        catch (e) { answer.threw = String(e?.message ?? e); }
        console.log(JSON.stringify(answer));`;

    const printed = execFileSync(process.execPath, ['--input-type=module', '--eval', script], {
        encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'],
    });

    const line = printed.trim().split('\n').filter(l => l.startsWith('{')).at(-1);
    assert.ok(line, `the probe printed nothing usable:\n${printed}`);
    const result = JSON.parse(line);

    // ★ It must TYPE-CHECK. The refusal belongs at the moment the program tries to run, not to a
    // checker pretending the form does not exist — a squiggle here would tell a reader the program
    // is wrong, when what is wrong is the place they are running it.
    assert.equal(result.check.trim(), '', `Check reported something on a valid program:\n${result.check}`);

    const said = String(result.ran ?? result.threw ?? '');

    // The fault a visitor actually saw, kept as an assertion because it would come back silently.
    assert.ok(!said.includes('PlatformNotSupported'),
        `a .NET resource key reached the reader:\n${said}`);

    assert.ok(said.includes('cannot run here'),
        `expected the Cufet refusal, got:\n${said}`);
    assert.ok(said.includes("cufet build"),
        `the refusal should say where the form DOES work, got:\n${said}`);
});
