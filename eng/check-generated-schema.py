"""Compare an isolated SchemaGen output with the committed public bindings."""
import argparse
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('generated', type=Path)
args = parser.parse_args()
expected = Path(__file__).resolve().parents[1] / 'managed/CounterStrikeSharp.API/Generated/Schema'
def files(root):
    return {p.relative_to(root).as_posix(): p.read_bytes().replace(b'\r\n', b'\n') for p in root.rglob('*.cs')}
generated, committed = files(args.generated), files(expected)
assert generated, 'No generated files'
assert generated.keys() == committed.keys(), 'Generated file list differs'
different = [name for name in generated if generated[name] != committed[name]]
assert not different, different
print(f'All {len(generated)} generated schema files match.')
