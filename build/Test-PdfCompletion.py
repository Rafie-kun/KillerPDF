"""Disposable source-backed completion fixtures. Never run release scripts or contact a remote."""
import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

sys.dont_write_bytecode = True

ROOT = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('completion', ROOT / 'Check-PdfCompletion.py')
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)

class CompletionTests(unittest.TestCase):
    def setUp(self):
        self.repo = Path(tempfile.mkdtemp(prefix='killerpdf-gate-fixture-'))
        self.git('init', '-b', 'main')
        self.git('config', 'user.name', 'Steve the Killer')
        self.git('config', 'user.email', 'steve@thekiller.net')
        self.git('config', 'commit.gpgsign', 'false')
        self.git('config', 'core.autocrlf', 'false')
        self.git('config', 'core.hooksPath', str(self.repo / 'empty-hooks'))
        (self.repo / 'fixture.txt').write_text('baseline\n')
        self.commit('baseline')
        self.baseline = self.tip()
        (self.repo / 'fixture.txt').write_text('fixed\n')
        self.commit('v1.8.90: fix')
        self.main = self.tip()
        self.git('checkout', '-b', 'dev/1.9-overkill', self.baseline)
        (self.repo / 'fixture.txt').write_text('adapted fixed\n')
        (self.repo / 'build').mkdir()
        (self.repo / gate.SCRIPT).write_bytes((ROOT / 'Check-PdfCompletion.py').read_bytes())
        (self.repo / gate.CHECKER).write_bytes((ROOT / 'Check-MaintenanceForwardPorts.ps1').read_bytes())
        (self.repo / '.github').mkdir()
        self.policy = dict(schemaVersion=1, maintenanceBaseline=self.baseline, developmentBaseline=self.baseline, ports=[])
        (self.repo / gate.POLICY).write_text(json.dumps(self.policy))
        self.commit('v1.9.0: adapted fix')
        self.adapted = self.tip()
        self.policy['ports'] = [dict(maintenanceCommit=self.main, developmentCommit=self.adapted, reason='Adapted engine path.')]
        (self.repo / gate.POLICY).write_text(json.dumps(self.policy))
        self.commit('v1.9.0: record adapted fix')
        self.dev = self.tip()
        self.tips = dict(maintenance=self.main, development=self.dev)
        assertions = {}
        for line, tip in self.tips.items():
            data = gate.blob(self.repo, tip, 'fixture.txt')
            assertions[line] = [dict(path='fixture.txt', sha256=gate.digest(data), contains=['fixed'])]
        self.review = dict(schemaVersion=1, tips=self.tips, entries=[
            dict(maintenanceCommit=self.main, status='adapted', reason='Same regression through changed engine API.',
                 assertions=assertions, tests={line:['Regression'] for line in self.tips})])
        self.results = []
        for line, tip in self.tips.items():
            for index, project in enumerate(gate.PROJECTS):
                path = self.repo / (line + str(index) + '.trx')
                path.write_text('<TestRun><Results><UnitTestResult testName="Regression" outcome="Passed"/></Results>'
                                '<ResultSummary><Counters total="1" passed="1" failed="0" notExecuted="0"/></ResultSummary></TestRun>')
                self.results.append(dict(line=line, project=project, commit=tip, exitCode=0, trx=str(path),
                                         sha256=gate.digest(path.read_bytes())))
        self.receipt = dict(schemaVersion=1, gateSha256=gate.digest((ROOT / 'Check-PdfCompletion.py').read_bytes()),
                            checkerSha256=gate.digest((ROOT / gate.CHECKER.split('/')[-1]).read_bytes()),
                            review=self.review, results=self.results)
        self.save_receipt()

    def git(self, *args):
        result = subprocess.run(['git', '-C', str(self.repo), *args], capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        return result.stdout.strip()

    def tip(self):
        return self.git('rev-parse', 'HEAD')

    def commit(self, subject):
        self.git('add', '--', '.')
        self.git('commit', '-m', subject)

    def save_receipt(self):
        (self.repo / '.git' / 'killerpdf-completion.json').write_text(json.dumps(self.receipt))

    def cli(self, expected=True):
        result = subprocess.run([sys.executable, str(self.repo / gate.SCRIPT), '--repo', str(self.repo),
                                 '--verify-only'], capture_output=True, text=True)
        self.assertEqual(result.returncode == 0, expected, result.stdout + result.stderr)
        return result

    def test_valid_pinned_source_and_four_test_results(self):
        self.cli()

    def test_missing_receipt_and_stale_tips_block(self):
        (self.repo / '.git' / 'killerpdf-completion.json').unlink()
        self.cli(False)
        self.save_receipt()
        self.receipt['review']['tips']['maintenance'] = self.baseline
        self.save_receipt()
        self.cli(False)

    def test_unknown_missing_and_duplicate_reviews_block(self):
        for mutate in (
            lambda r: r['entries'][0].update(status='unresolved'),
            lambda r: r.update(entries=[]),
            lambda r: r['entries'].append(copy.deepcopy(r['entries'][0])),
        ):
            review = copy.deepcopy(self.review)
            mutate(review)
            with self.assertRaises(ValueError):
                gate.validate_review(self.repo, self.tips, review)

    def test_unrelated_record_is_not_exact_evidence(self):
        review = copy.deepcopy(self.review)
        review['entries'][0]['status'] = 'exact'
        with self.assertRaisesRegex(ValueError, 'no matching patch'):
            gate.validate_review(self.repo, self.tips, review)

    def test_bad_hash_missing_behavior_and_wrong_absence_block(self):
        for values in ({'sha256':'0'*64}, {'contains':['unimplemented behavior']}, {'contains':[]}, {'absent':True}):
            review = copy.deepcopy(self.review)
            review['entries'][0]['assertions']['development'][0].update(values)
            with self.assertRaises(ValueError):
                gate.validate_review(self.repo, self.tips, review)

    def test_failed_missing_stale_skipped_and_changed_test_evidence_block(self):
        for mutate in (
            lambda r: r.pop(),
            lambda r: r[0].update(commit=self.baseline),
            lambda r: r[0].update(exitCode=1),
            lambda r: r[0].update(sha256='0'*64),
            lambda r: r.append(copy.deepcopy(r[0])),
        ):
            results = copy.deepcopy(self.results)
            mutate(results)
            with self.assertRaises(ValueError):
                gate.validate_results(self.tips, self.review, results)
        path = Path(self.results[0]['trx'])
        for outcome in ('Failed', 'NotExecuted'):
            path.write_text('<TestRun><UnitTestResult testName="Regression" outcome="'+outcome+'"/></TestRun>')
            with self.assertRaises(ValueError):
                gate.trx_tests(path)

    def test_named_regression_must_run_on_both_lines(self):
        review = copy.deepcopy(self.review)
        review['entries'][0]['tests']['development'] = ['NotRun']
        with self.assertRaisesRegex(ValueError, 'not executed'):
            gate.validate_results(self.tips, review, self.results)

    def test_dirty_policy_cannot_hide_missing_port(self):
        self.policy['ports'] = []
        (self.repo / gate.POLICY).write_text(json.dumps(self.policy))
        self.cli()  # committed policy still carries the valid record
        self.commit('v1.9.0: remove port record')
        self.dev = self.tip()
        self.tips['development'] = self.dev
        for result in self.results:
            if result['line'] == 'development':
                result['commit'] = self.dev
        self.save_receipt()
        self.cli(False)  # exact new committed policy lacks coverage

    def test_changed_gate_and_missing_checker_block(self):
        (self.repo / gate.SCRIPT).write_text('raise SystemExit("changed gate")')
        self.cli(False)
        (self.repo / gate.SCRIPT).write_bytes((ROOT / 'Check-PdfCompletion.py').read_bytes())
        self.git('rm', '--', gate.CHECKER)
        self.commit('v1.9.0: remove checker')
        self.cli(False)

    def test_runner_uses_four_exact_committed_snapshots(self):
        calls = []
        def fake_run(command, **kwargs):
            calls.append(command)
            source = Path(kwargs['cwd']) / 'fixture.txt'
            expected = 'baseline'  # ensures test code inspects actual archived source
            self.assertNotIn(expected, source.read_text())
            folder = Path(command[-1])
            folder.joinpath('results.trx').write_text(Path(self.results[0]['trx']).read_text())
            return subprocess.CompletedProcess(command, 0)
        output = Path(tempfile.mkdtemp(prefix='killerpdf-runner-fixture-'))
        # Mock only dotnet execution; Git archive and snapshot extraction remain real.
        def dispatch(command, **kwargs):
            if command[0] == 'dotnet':
                return fake_run(command, **kwargs)
            return original_run(command, **kwargs)
        original_run = subprocess.run
        with mock.patch.object(gate.subprocess, 'run', side_effect=dispatch):
            results = gate.run_suites(self.repo, self.tips, output)
        self.assertEqual(len(calls), 4)
        gate.validate_results(self.tips, self.review, results)

    def test_real_local_pre_push_blocks_and_then_accepts_complete_pair(self):
        harness = self.repo / 'harness'
        (harness / 'scripts').mkdir(parents=True)
        (harness / 'config').mkdir()
        (harness / 'git-hooks').mkdir()
        source = Path.home() / 'code/KillerHarness/scripts/Check-GitCommit.py'
        (harness / 'scripts/Check-GitCommit.py').write_bytes(source.read_bytes())
        # The actual guard scopes the gate to the registered PDF path.
        pdf = self.repo / 'KillerPDF'
        (pdf / 'build').mkdir(parents=True)
        (pdf / gate.SCRIPT).write_bytes((ROOT / 'Check-PdfCompletion.py').read_bytes())
        self.git('worktree', 'add', '--detach', str(self.repo / 'other-worktree'), self.main)
        policy = dict(owner_identities=[dict(name='Steve the Killer', email='steve@thekiller.net')],
                      repositories=[dict(path=str(pdf), git_common_dir=str(self.repo / '.git'),
                                         accepted_history_tips=[self.baseline], reviewed_contributor_commits=[])])
        (harness / 'config/git-commit-policy.json').write_text(json.dumps(policy))
        hook = harness / 'git-hooks/pre-push'
        hook.write_text('#!/bin/sh\nexec "'+Path(sys.executable).as_posix()+'" "'+
                        (harness / 'scripts/Check-GitCommit.py').as_posix()+'" push "$@"\n')
        self.git('config', 'core.hooksPath', str(harness / 'git-hooks'))
        # Gate needs a Git working directory, not a fabricated standalone path.
        (pdf / '.git').write_text('gitdir: '+str(self.repo / '.git')+'\n')
        remote = Path(tempfile.mkdtemp(prefix='killerpdf-bare-fixture-')) / 'remote.git'
        self.git('init', '--bare', str(remote))
        (self.repo / '.git/killerpdf-completion.json').unlink()
        command = ['git', '-C', str(self.repo), 'push', str(remote), 'main', 'dev/1.9-overkill']
        rejected = subprocess.run(command, capture_output=True, text=True)
        self.assertNotEqual(rejected.returncode, 0)
        self.assertIn('PDF completion', rejected.stderr)
        self.assertEqual(self.git('ls-remote', str(remote)), '')
        self.save_receipt()
        command[2] = str(self.repo / 'other-worktree')
        accepted = subprocess.run(command, capture_output=True, text=True)
        self.assertEqual(accepted.returncode, 0, accepted.stderr)
        # Local paired work can accumulate, but cannot push with stale evidence.
        self.git('checkout', 'main')
        self.git('commit', '--allow-empty', '-m', 'v1.8.90: later paired work')
        stale = subprocess.run(command, capture_output=True, text=True)
        self.assertNotEqual(stale.returncode, 0)
        self.assertIn('stale', stale.stderr)
        self.assertEqual(self.git('--git-dir='+str(remote), 'rev-parse', 'refs/heads/main'), self.main)

    def test_inherited_git_environment_does_not_change_evidence(self):
        with mock.patch.dict(gate.os.environ, {'GIT_DIR':str(self.repo / 'missing-git-dir'),
                                              'GIT_WORK_TREE':str(self.repo / 'wrong-tree')}):
            gate.validate_review(self.repo, self.tips, self.review)

    def test_git_newline_conversion_does_not_invalidate_committed_gate(self):
        path = self.repo / gate.SCRIPT
        path.write_bytes(path.read_bytes().replace(b'\n', b'\r\n'))
        self.cli()

    def test_failed_runner_cannot_leave_successful_receipt(self):
        review_path = self.repo / 'review.json'
        review_path.write_text(json.dumps(self.review))
        argv = ['Check-PdfCompletion.py', '--repo', str(self.repo), '--run-tests', '--review', str(review_path)]
        with mock.patch.object(gate, '__file__', str(self.repo / gate.SCRIPT)), mock.patch.object(sys, 'argv', argv), mock.patch.object(gate, 'run_suites', side_effect=ValueError('fixture test failure')):
            with self.assertRaisesRegex(ValueError, 'fixture test failure'):
                gate.main()
        self.assertFalse((self.repo / '.git/killerpdf-completion.json').exists())

    def test_successful_runner_atomically_replaces_receipt(self):
        review_path = self.repo / 'review.json'
        review_path.write_text(json.dumps(self.review))
        argv = ['Check-PdfCompletion.py', '--repo', str(self.repo), '--run-tests', '--review', str(review_path)]
        with mock.patch.object(gate, '__file__', str(self.repo / gate.SCRIPT)), mock.patch.object(sys, 'argv', argv), mock.patch.object(gate, 'run_suites', return_value=self.results):
            self.assertEqual(gate.main(), 0)
        self.assertFalse(list((self.repo / '.git').glob('killerpdf-completion-*.tmp')))
        self.cli()

    def test_release_preflight_is_before_side_effects_and_returns(self):
        # Inspect the real script; never execute a release script in fixtures.
        release = (ROOT.parent / 'release.ps1').read_text(encoding='utf-8-sig')
        start = release.index('$completionGate =')
        finish = release.index('if ($PreflightOnly) { return }', start)
        self.assertLess(finish, release.index('$proj         ='))
        preflight = release[start:finish]
        for mutation in ('git fetch', 'git push', 'dotnet', 'signtool', 'Start-Process', 'Set-Content'):
            self.assertNotIn(mutation, preflight)

if __name__ == '__main__':
    unittest.main(verbosity=2)
