"""KillerPDF completion gate. Verify is read-only; run-tests writes temporary build/test output.
Local evidence prevents accidental omission. It is not a security or semantic proof boundary.
"""
import argparse
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import sys
import tarfile
import tempfile
import xml.etree.ElementTree as ET

PROJECTS = ('KillerPDF.Tests/KillerPDF.Tests.csproj',
            'engine/KillerPdf.Engine.Tests/KillerPdf.Engine.Tests.csproj')
SCRIPT = 'build/Check-PdfCompletion.py'
CHECKER = 'build/Check-MaintenanceForwardPorts.ps1'
POLICY = '.github/maintenance-forward-ports.json'

def digest(data):
    return hashlib.sha256(data).hexdigest()

def git(repo, *args, optional=False):
    environment = {key: value for key, value in os.environ.items() if not key.startswith('GIT_')}
    result = subprocess.run(['git', '-C', str(repo), *args], capture_output=True, env=environment)
    if result.returncode and not optional:
        raise ValueError(result.stderr.decode('utf-8', 'replace').strip())
    return result

def text(repo, *args):
    return git(repo, *args).stdout.decode('utf-8').strip()

def resolve(repo, ref):
    return text(repo, 'rev-parse', '--verify', '--end-of-options', ref + '^{commit}')

def ancestor(repo, older, newer):
    result = git(repo, 'merge-base', '--is-ancestor', older, newer, optional=True)
    if result.returncode not in (0, 1):
        raise ValueError('Cannot verify ancestry.')
    return result.returncode == 0

def blob(repo, tip, path):
    parts = PurePosixPath(path)
    if not path or parts.is_absolute() or '..' in parts.parts or '\\' in path or ':' in path:
        raise ValueError('Evidence requires a repository-relative path.')
    result = git(repo, 'show', tip + ':' + path, optional=True)
    if result.returncode:
        # An absent file must actually be absent, not an unreadable Git object.
        exists = text(repo, 'ls-tree', tip, '--', path)
        if exists:
            raise ValueError('Cannot read evidence object: ' + path)
        return None
    return result.stdout

def read_policy(repo, development):
    raw = blob(repo, development, POLICY)
    if raw is None:
        raise ValueError('Missing committed development policy.')
    policy = json.loads(raw)
    if policy.get('schemaVersion') != 1:
        raise ValueError('Unsupported policy.')
    return policy

def validate_review(repo, tips, review):
    if review.get('schemaVersion') != 1 or review.get('tips') != tips:
        raise ValueError('Missing or stale source review: exact commit IDs must match.')
    policy = read_policy(repo, tips['development'])
    baseline = resolve(repo, policy['maintenanceBaseline'])
    dev_baseline = resolve(repo, policy['developmentBaseline'])
    if not ancestor(repo, baseline, tips['maintenance']) or not ancestor(repo, dev_baseline, tips['development']):
        raise ValueError('Reviewed baselines are not on the checked lines.')
    commits = set(text(repo, 'rev-list', baseline + '..' + tips['maintenance']).splitlines())
    equivalents = {line[2:] for line in text(repo, 'cherry', tips['development'], tips['maintenance'], baseline).splitlines()
                   if line.startswith('- ')}
    seen = set()
    for record in review.get('entries', []):
        source = record.get('maintenanceCommit')
        if source not in commits or source in seen:
            raise ValueError('Unknown or duplicate review entry.')
        seen.add(source)
        if record.get('status') not in ('exact', 'adapted', 'inapplicable'):
            raise ValueError('Unresolved parity: ' + source)
        if record['status'] == 'exact' and source not in equivalents and not ancestor(repo, source, tips['development']):
            raise ValueError('Exact review has no matching patch or shared ancestor.')
        if not str(record.get('reason', '')).strip():
            raise ValueError('Review needs a concrete rationale.')
        for line, tip in tips.items():
            assertions = record.get('assertions', {}).get(line, [])
            tests = record.get('tests', {}).get(line, [])
            if not assertions or not tests or any(not isinstance(t, str) or not t.strip() for t in tests):
                raise ValueError('Every entry needs source assertions and named regression tests on both lines.')
            for assertion in assertions:
                actual = blob(repo, tip, assertion['path'])
                if assertion.get('absent') is True:
                    if actual is not None:
                        raise ValueError('Expected absent source exists: ' + assertion['path'])
                elif actual is None or digest(actual) != assertion.get('sha256'):
                    raise ValueError('Source evidence mismatch: ' + assertion['path'])
                elif not assertion.get('contains') or any(needle.encode('utf-8') not in actual for needle in assertion['contains']):
                    raise ValueError('Missing or mismatched behavior assertion: ' + assertion['path'])
    if seen != commits:
        raise ValueError('Missing parity review entries: ' + str(len(commits - seen)))
    return policy

def trx_tests(path):
    root = ET.parse(path).getroot()
    results = root.findall('.//{*}UnitTestResult')
    if not results or any(r.get('outcome') != 'Passed' for r in results):
        raise ValueError('TRX contains missing, failed, skipped, or incomplete tests.')
    counters = root.find('.//{*}Counters')
    if counters is None or int(counters.get('total', '0')) != len(results) or int(counters.get('passed', '0')) != len(results):
        raise ValueError('Incomplete TRX counters.')
    if any(int(counters.get(key, '0')) for key in ('failed', 'error', 'timeout', 'aborted', 'notExecuted')):
        raise ValueError('TRX reports failed or unexecuted tests.')
    return {r.get('testName') for r in results}

def validate_results(tips, review, results):
    seen = set()
    tested = {line: set() for line in tips}
    for result in results:
        key = (result.get('line'), result.get('project'))
        if key in seen or key[0] not in tips or key[1] not in PROJECTS:
            raise ValueError('Unknown or duplicate test result.')
        seen.add(key)
        if result.get('commit') != tips[key[0]] or result.get('exitCode') != 0:
            raise ValueError('Stale or failed test execution.')
        path = Path(result['trx'])
        if digest(path.read_bytes()) != result.get('sha256'):
            raise ValueError('Changed or missing test artifact.')
        tested[key[0]].update(trx_tests(path))
    if seen != {(line, project) for line in tips for project in PROJECTS}:
        raise ValueError('Both application and engine suites must pass on both lines.')
    for record in review['entries']:
        for line in tips:
            if not set(record['tests'][line]).issubset(tested[line]):
                raise ValueError('Named parity regression was not executed: ' + record['maintenanceCommit'])

def coverage(repo, tips, checker):
    host = shutil.which('pwsh') or shutil.which('powershell')
    if not host:
        raise ValueError('PowerShell is required for the existing coverage checker.')
    result = subprocess.run([host, '-NoProfile', '-File', str(checker),
        '-MaintenanceRef', tips['maintenance'], '-DevelopmentRef', tips['development'],
        '-PolicyRef', tips['development']], cwd=repo)
    if result.returncode:
        raise ValueError('Maintenance forward-port coverage failed.')

def run_suites(repo, tips, output):
    results = []
    for line, tip in tips.items():
        snapshot = output / line
        snapshot.mkdir()
        data = git(repo, 'archive', '--format=tar', tip).stdout
        with tarfile.open(fileobj=io.BytesIO(data)) as archive:
            archive.extractall(snapshot, filter='data')
        for index, project in enumerate(PROJECTS):
            folder = output / (line + '-tests-' + str(index))
            folder.mkdir()
            environment = {key: value for key, value in os.environ.items() if not key.startswith('GIT_')}
            run = subprocess.run(['dotnet', 'test', str(snapshot / project), '-c', 'Release',
                '--logger', 'trx;LogFileName=results.trx', '--results-directory', str(folder)], cwd=snapshot, env=environment)
            if run.returncode:
                raise ValueError('Test execution failed: ' + line + ' ' + project)
            trx = folder / 'results.trx'
            trx_tests(trx)
            results.append(dict(line=line, project=project, commit=tip, exitCode=run.returncode,
                                trx=str(trx), sha256=digest(trx.read_bytes())))
    return results

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', default='.')
    parser.add_argument('--maintenance-ref', default='refs/heads/main')
    parser.add_argument('--development-ref', default='refs/heads/dev/1.9-overkill')
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument('--verify-only', action='store_true')
    mode.add_argument('--run-tests', action='store_true')
    parser.add_argument('--review', help='Reviewed parity JSON with source hashes and named tests.')
    args = parser.parse_args()
    repo = Path(args.repo).resolve()
    tips = dict(maintenance=resolve(repo, args.maintenance_ref),
                development=resolve(repo, args.development_ref))
    common = Path(text(repo, 'rev-parse', '--path-format=absolute', '--git-common-dir'))
    receipt_path = common / 'killerpdf-completion.json'
    # Compare the running gate with the committed development implementation.
    script_bytes = Path(__file__).read_bytes()
    committed_script = blob(repo, tips['development'], SCRIPT)
    if committed_script is None or committed_script.replace(b'\r\n', b'\n') != script_bytes.replace(b'\r\n', b'\n'):
        raise ValueError('Completion gate is not the committed development implementation.')
    # Git autocrlf changes working newlines without changing Python behavior.
    # Receipts bind the committed bytes, not the checkout's newline conversion.
    script_bytes = committed_script
    checker_bytes = blob(repo, tips['development'], CHECKER)
    if checker_bytes is None:
        raise ValueError('Missing committed forward-port checker.')
    if args.run_tests:
        if not args.review:
            raise ValueError('Run-tests requires an explicit source/behavior review.')
        review = json.loads(Path(args.review).read_text(encoding='utf-8-sig'))
        validate_review(repo, tips, review)
    else:
        receipt = json.loads(receipt_path.read_text(encoding='utf-8'))
        if receipt.get('schemaVersion') != 1 or receipt.get('gateSha256') != digest(script_bytes) or receipt.get('checkerSha256') != digest(checker_bytes):
            raise ValueError('Missing or stale gate evidence.')
        review = receipt['review']
        validate_review(repo, tips, review)
        validate_results(tips, review, receipt['results'])
    # Materialize only the exact committed checker, never a mutable/stale checkout copy.
    with tempfile.TemporaryDirectory(prefix='killerpdf-coverage-') as temp:
        checker = Path(temp) / 'Check-MaintenanceForwardPorts.ps1'
        checker.write_bytes(checker_bytes)
        coverage(repo, tips, checker)
    if args.run_tests:
        # Invalidate an older receipt before running tests on the same tips.
        receipt_path.unlink(missing_ok=True)
        output = Path(tempfile.mkdtemp(prefix='killerpdf-completion-'))
        results = run_suites(repo, tips, output)
        validate_results(tips, review, results)
        if tips != dict(maintenance=resolve(repo, args.maintenance_ref), development=resolve(repo, args.development_ref)):
            raise ValueError('Branches changed during verification; rerun for current tips.')
        receipt = dict(schemaVersion=1, gateSha256=digest(script_bytes),
                       checkerSha256=digest(checker_bytes), review=review, results=results)
        with tempfile.NamedTemporaryFile(mode='w', encoding='utf-8', prefix='killerpdf-completion-', suffix='.tmp', dir=common, delete=False) as handle:
            handle.write(json.dumps(receipt, indent=2) + '\n')
            pending = Path(handle.name)
        pending.replace(receipt_path)
    if tips != dict(maintenance=resolve(repo, args.maintenance_ref), development=resolve(repo, args.development_ref)):
        raise ValueError('Branches changed during verification.')
    print('PDF completion passed for exact maintenance/development tips, source evidence, and four test suites.')
    return 0

if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception as exc:
        print('PDF completion blocked: ' + str(exc), file=sys.stderr)
        sys.exit(1)
