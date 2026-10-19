"""Exercise the real batch CLI and inert arithmetic import in an isolated LibreOffice profile."""
import argparse
import csv
import json
import pathlib
import subprocess
import zipfile
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument('app')
parser.add_argument('root')
parser.add_argument('--libreoffice', default=r'C:\Program Files\LibreOffice\program\soffice.com')
args = parser.parse_args()
root = pathlib.Path(args.root).resolve()
root.mkdir(parents=True, exist_ok=False)

objects = [b'<< /Type /Catalog /Pages 2 0 R >>', b'<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
           b'<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>']
pdf = b'%PDF-1.4\n'
offsets = [0]
for index, obj in enumerate(objects, 1):
    offsets.append(len(pdf))
    pdf += str(index).encode() + b' 0 obj\n' + obj + b'\nendobj\n'
xref = len(pdf)
pdf += b'xref\n0 4\n0000000000 65535 f \n'
pdf += b''.join(f'{offset:010d} 00000 n \n'.encode() for offset in offsets[1:])
pdf += f'trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n'.encode()

cases = [('=1+1', "'=1+1"), ('+1+1', "'+1+1"), ('-1+1', "'-1+1"),
         ('@SUM(1,1)', "'@SUM(1,1)"), ('  =1+1', "'  =1+1"),
         ('ordinary.pdf', 'ordinary.pdf'), ('comma,name.pdf', 'comma,name.pdf')]
for number, (name, expected) in enumerate(cases):
    case = root / str(number)
    case.mkdir()
    source = case / name
    source.write_bytes(pdf)
    subprocess.run(['dotnet', args.app, '--batch-resave', str(source), str(case / 'saved.pdf'),
                    '--log', str(case / 'report.csv'), '--quiet'], timeout=30, check=True)
    with (case / 'report.csv').open(newline='', encoding='utf-8') as stream:
        rows = list(csv.reader(stream))
    assert rows == [['File', 'Status', 'Detail'], [expected, 'OK', '']], rows
    assert source.read_bytes() == pdf, 'Input changed'
    print('PASS CSV cell:', repr(name))

case = root / '0'
result = subprocess.run([args.libreoffice, '-env:UserInstallation=' + (root / 'profile').as_uri(),
    '--headless', '--infilter=Text - txt - csv (StarCalc):44,34,76,1,,0,false,true,true,false,false,0,true',
    '--convert-to', 'xlsx:Calc MS Excel 2007 XML', '--outdir', str(case), str(case / 'report.csv')],
    capture_output=True, text=True, timeout=45, check=True)
print(result.stdout, result.stderr)
ns = {'s': 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'}
with zipfile.ZipFile(case / 'report.xlsx') as archive:
    sheet = ET.fromstring(archive.read('xl/worksheets/sheet1.xml'))
    cell = sheet.find('.//s:c[@r="A2"]', ns)
    formula = cell.find('s:f', ns)
    value = cell.find('s:v', ns)
    shared = ET.fromstring(archive.read('xl/sharedStrings.xml'))
    strings = [''.join(node.itertext()) for node in shared]
    text = strings[int(value.text)] if cell.get('t') == 's' else None
    record = {'formula': formula.text if formula is not None else None,
              'value': value.text if value is not None else None, 'text': text}
    print(json.dumps(record, indent=2))
    (root / 'spreadsheet-result.json').write_text(json.dumps(record, indent=2))
    assert formula is None and text in ('=1+1', "'=1+1"), 'Filename evaluated as formula'
print('PASS supported Calc import retains the inert arithmetic filename as text with formula evaluation enabled.')
