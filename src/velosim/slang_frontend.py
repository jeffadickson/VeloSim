"""slang frontend adapter: elaboration/diagnostics/AST export, NOT HDL execution."""
from __future__ import annotations
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

class SlangError(RuntimeError):
    pass

def compile_ast(sources, *, top=None, slang=None, std='1800-2023', extra=(), timeout=120):
    """Run real slang elaboration; return AST JSON, fail closed on errors."""
    executable = slang or os.environ.get('VELOSIM_SLANG') or shutil.which('slang')
    if not executable:
        raise SlangError('slang executable not found; install slang or set VELOSIM_SLANG')
    if not sources:
        raise ValueError('at least one source file required')
    with tempfile.TemporaryDirectory(prefix='velosim-slang-') as directory:
        output = Path(directory) / 'ast.json'
        args = [str(executable), '--std', std, '--ast-json', str(output), '--ast-json-source-info']
        if top:
            args += ['--top', top]
        args += list(extra)
        args += [str(Path(s).resolve()) for s in sources]
        try:
            proc = subprocess.run(args, capture_output=True, text=True, timeout=timeout, check=False)
        except (OSError, subprocess.TimeoutExpired) as exc:
            raise SlangError(f'slang invocation failed: {exc}') from exc
        if proc.returncode:
            raise SlangError(f'slang elaboration failed (exit {proc.returncode}):\n{proc.stderr}\n{proc.stdout}')
        if not output.exists():
            raise SlangError('slang succeeded without producing an AST')
        try:
            data = json.loads(output.read_text(encoding='utf-8'))
        except (ValueError, OSError) as exc:
            raise SlangError(f'invalid slang AST: {exc}') from exc
        if not isinstance(data, dict) or not isinstance(data.get('design'), dict):
            raise SlangError('unexpected slang AST schema: missing design object')
        return data

def summarize(data):
    """Enumerate elaborated top instances only; no speculative lowering."""
    root = data['design']
    return {'root_kind': root.get('kind'), 'top_instances': [
        item.get('name') for item in root.get('members', [])
        if isinstance(item, dict) and item.get('kind') == 'Instance'
    ]}

def main(argv=None):
    parser = argparse.ArgumentParser(description='VeloSim slang elaboration bridge')
    parser.add_argument('sources', nargs='+')
    parser.add_argument('--top')
    parser.add_argument('--slang')
    parser.add_argument('--ast-out', type=Path)
    args = parser.parse_args(argv)
    try:
        ast = compile_ast(args.sources, top=args.top, slang=args.slang)
    except (SlangError, ValueError) as exc:
        parser.exit(1, f'{exc}\n')
    if args.ast_out:
        args.ast_out.write_text(json.dumps(ast, indent=2), encoding='utf-8')
    print(json.dumps(summarize(ast), indent=2))
    return 0

if __name__ == '__main__':
    raise SystemExit(main())
