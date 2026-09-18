"""Executes only extracted pure/helper functions with mocked OS/command access. Never imports the remote entrypoint."""
import ast, pathlib, unittest, types, hashlib, re, shlex, json, os, time, tempfile
from unittest.mock import Mock, patch

source = pathlib.Path(__file__).parents[1] / 'RemoteAssist' / 'Scripts' / 'AstraAdmin.py'
module = ast.parse(source.read_text(encoding='utf-8'))
functions = ast.Module(body=[n for n in module.body if isinstance(n,ast.FunctionDef)],type_ignores=[])

class HelperTests(unittest.TestCase):
    def setUp(self):
        self.config = """menuentry 'Astra Linux' $menuentry_id_option 'astra-id' {
}
menuentry 'Windows Boot Manager' $menuentry_id_option 'windows-id' {
}
"""
        self.defaults = 'GRUB_DEFAULT=0\nGRUB_SAVEDEFAULT=false'
        self.fs='ext4 /dev/sda2'
        self.env=''
        self.calls=[]
        self.ns=dict(pathlib=pathlib,hashlib=hashlib,re=re,shlex=shlex,json=json,os=os,time=time,shutil=types.SimpleNamespace(which=lambda _: '/usr/bin/tool'))
        exec(compile(functions,str(source),'exec'),self.ns)
        self.ns['read']=lambda path,fallback='': {'/boot/grub/grub.cfg':self.config,'/etc/default/grub':self.defaults}.get(str(path),fallback)
        self.ns['run']=self.run_command
    def run_command(self,args,**kwargs):
        self.calls.append(args)
        result=types.SimpleNamespace(returncode=0,stdout='',stderr='')
        if args[0]=='findmnt':result.stdout=self.fs
        if args[0]=='grub-editenv' and args[-1]=='list':result.stdout=self.env
        return result
    def test_valid_grub(self):
        info=self.ns['grub_info']()
        self.assertEqual(info['GrubReady'],'yes');self.assertEqual(info['WindowsMenuIds'],'windows-id')
        self.assertEqual(len(info['BootConfigHash']),64)
    def test_lvm_fails_closed(self):
        self.fs='ext4 /dev/mapper/root';self.assertEqual(self.ns['grub_info']()['GrubReady'],'no')
    def test_existing_next_entry_not_overwritten(self):
        self.env='next_entry=another-os';self.assertEqual(self.ns['grub_info']()['GrubReady'],'no')
    def test_saved_default_rejected(self):
        self.defaults='GRUB_DEFAULT=saved';self.assertEqual(self.ns['grub_info']()['GrubReady'],'no')
    def test_windows_default_rejected(self):
        self.defaults="GRUB_DEFAULT='windows-id'";self.assertEqual(self.ns['grub_info']()['GrubReady'],'no')
    def test_save_last_selection_rejected(self):
        self.defaults='GRUB_DEFAULT=0\nGRUB_SAVEDEFAULT=true';self.assertEqual(self.ns['grub_info']()['GrubReady'],'no')
    def test_config_change_invalidates_pilot_hash(self):
        before=self.ns['grub_info']()['BootConfigHash'];self.config+='\n# update';self.assertNotEqual(before,self.ns['grub_info']()['BootConfigHash'])
    def test_grub_syntax_failure(self):
        original=self.run_command
        def command(args,**kwargs):
            result=original(args,**kwargs)
            if args[0]=='grub-script-check':result.returncode=1
            return result
        self.ns['run']=command;self.assertEqual(self.ns['grub_info']()['GrubReady'],'no')
    def test_power_requires_matching_pilot(self):
        self.ns.update(payload=dict(Action='BootWindows',WindowsMenuId='windows-id',BootPilotVerified=True,BootConfigHash='wrong'),warning=lambda:None,verify_identity=lambda:None,cancelled=lambda:None)
        with self.assertRaisesRegex(RuntimeError,'не проверен'):self.ns['power']()
        self.assertFalse(any(c[0]=='grub-reboot' for c in self.calls))
    def test_inhibitor_prevents_shutdown(self):
        self.ns.update(payload=dict(Action='Shutdown'),warning=lambda:None,verify_identity=lambda:None,cancelled=lambda:None)
        self.ns['run']=lambda args,**kwargs: types.SimpleNamespace(returncode=0,stdout='a(ssssuu) 1 "shutdown" "editor" "unsaved files" "block" 1000 42',stderr='') if args[0]=='busctl' else self.run_command(args,**kwargs)
        with self.assertRaisesRegex(RuntimeError,'заблокировано'):self.ns['power']()
        self.assertFalse(any(c[0]=='systemctl' for c in self.calls))
    def test_package_name_not_shell(self):
        with self.assertRaisesRegex(RuntimeError,'Некорректное'):self.ns['package_version']('hello;reboot')
        self.assertEqual(self.calls,[])
    def test_dpkg_installed_state(self):
        self.ns['run']=lambda args,**kw:types.SimpleNamespace(returncode=0,stdout='deinstall ok config-files\n1.2')
        self.assertEqual(self.ns['package_version']('test'),'')
        self.ns['run']=lambda args,**kw:types.SimpleNamespace(returncode=0,stdout='install ok installed\n1.2')
        self.assertEqual(self.ns['package_version']('test'),'1.2')
    def test_wrong_sha_never_calls_apt(self):
        with tempfile.TemporaryDirectory() as tmp:
            directory=pathlib.Path(tmp);(directory/'package.deb').write_bytes(b'test')
            self.ns.update(DIRECTORY=directory,payload={'Package':dict(Os='Astra',Kind='DEB',Architecture='any',Detection='test',Version='1',Sha256='0'*64)},package_version=lambda _: '',os=types.SimpleNamespace(uname=lambda:types.SimpleNamespace(machine='x86_64')))
            with self.assertRaisesRegex(RuntimeError,'SHA-256'):self.ns['install']()
            self.assertFalse(any(c[0]=='apt-get' for c in self.calls))

if __name__ == '__main__':unittest.main(verbosity=2)
