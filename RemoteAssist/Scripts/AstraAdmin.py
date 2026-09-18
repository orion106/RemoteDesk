#!/usr/bin/env python3
"""One-shot administration helper. No installed daemon; persistent results allow reconciliation."""
import os, sys, json, subprocess, pathlib, hashlib, re, time, pwd, signal, shutil, shlex

REQUEST = pathlib.Path(sys.argv[1]).resolve()
DIRECTORY = REQUEST.parent
if not re.fullmatch(r'[a-f0-9]{32}', DIRECTORY.name) or DIRECTORY.parent.name != '.remote-desk':
    raise RuntimeError('Invalid request directory')
if '--worker' not in sys.argv:
    # The worker survives SSH disconnect; dpkg must never be killed because the console disappeared.
    subprocess.Popen([sys.executable, str(pathlib.Path(__file__).resolve()), str(REQUEST), '--worker'],
                     stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True, close_fds=True)
    sys.exit(0)
request = json.loads(REQUEST.read_text(encoding='utf-8-sig'))
payload, options = request.get('Payload', {}), request.get('Options', {})

def run(args, timeout=30, check=True, env=None):
    result = subprocess.run(args, capture_output=True, text=True, timeout=timeout, env=env)
    if check and result.returncode:
        raise RuntimeError('{}: {}'.format(args[0], result.stderr.strip()[:800]))
    return result

def read(path, fallback=''):
    try: return pathlib.Path(path).read_text().strip()
    except (OSError, UnicodeError): return fallback

def finish(state, detail, data=None):
    temporary = DIRECTORY / 'result.tmp'
    temporary.write_text(json.dumps(dict(State=state, Detail=detail, Data=data), ensure_ascii=False), encoding='utf-8')
    os.chmod(temporary, 0o644)
    temporary.replace(DIRECTORY / 'result.json')

def cancelled():
    if (DIRECTORY / 'cancel.flag').exists(): raise RuntimeError('Отменено до следующего изменения системы')

def norm(value): return str(value).strip().strip('{}').upper()

def verify_identity():
    confirmed = False
    for key, path in [('ExpectedUuid', '/sys/class/dmi/id/product_uuid'), ('ExpectedSerial', '/sys/class/dmi/id/product_serial')]:
        if request.get(key):
            if norm(read(path)) != norm(request[key]): raise RuntimeError('Аппаратный идентификатор изменился')
            confirmed = True
    if not confirmed: raise RuntimeError('Нет подтвержденного аппаратного идентификатора')
    release = read('/etc/os-release') + read('/etc/astra_version')
    if 'astra' not in release.lower() and not pathlib.Path('/etc/astra_version').exists(): raise RuntimeError('Текущая ОС не Astra Linux')

def sessions():
    result = []
    for row in run(['loginctl', 'list-sessions', '--no-legend', '--no-pager']).stdout.splitlines():
        if not row.strip(): continue
        sid = row.split()[0]
        info = run(['loginctl', 'show-session', sid, '-p', 'Id', '-p', 'Name', '-p', 'User', '-p', 'Type', '-p', 'Active', '-p', 'Remote', '-p', 'Display', '-p', 'Leader', '-p', 'Class', '-p', 'State']).stdout
        data = dict(line.split('=', 1) for line in info.splitlines() if '=' in line)
        if data.get('Class') == 'user' and data.get('Remote') == 'no': result.append(data)
    return result

def session_environment(session):
    if session.get('Type') != 'x11' or session.get('Active') != 'yes': raise RuntimeError('Нужен подтвержденный активный X11-сеанс')
    uid = int(session['User'])
    env = {}
    candidates = [session.get('Leader', '')] + [p.name for p in pathlib.Path('/proc').iterdir() if p.name.isdigit()]
    for pid in candidates:
        try:
            path = pathlib.Path('/proc') / pid
            if path.stat().st_uid != uid: continue
            values = dict(item.split('=', 1) for item in (path / 'environ').read_bytes().decode(errors='replace').split('\0') if '=' in item)
            if values.get('DISPLAY') and (values.get('XDG_SESSION_ID') == session['Id'] or values.get('DISPLAY') == session.get('Display')):
                env = values
                if values.get('XAUTHORITY'): break
        except (OSError, ValueError): pass
    display = env.get('DISPLAY') or session.get('Display')
    auth = env.get('XAUTHORITY')
    if not auth:
        candidate = pathlib.Path(pwd.getpwuid(uid).pw_dir) / '.Xauthority'
        if candidate.is_file() and candidate.stat().st_uid == uid: auth = str(candidate)
    if not display or not auth or not pathlib.Path(auth).is_file(): raise RuntimeError('Не удалось подтвердить DISPLAY/XAUTHORITY пользователя')
    return {'DISPLAY': display, 'XAUTHORITY': auth, 'DBUS_SESSION_BUS_ADDRESS': 'unix:path=/run/user/{}/bus'.format(uid), 'XDG_RUNTIME_DIR': '/run/user/{}'.format(uid)}

def as_user(session, args, timeout=130, check=True):
    env = session_environment(session)
    return run(['runuser', '-u', session['Name'], '--', 'env'] + [k+'='+v for k,v in env.items()] + args, timeout, check)

def session_key(items): return sorted((s.get('Id'), s.get('User'), s.get('Active'), s.get('Type')) for s in items)

def warning():
    before = sessions()
    for s in before:
        if s.get('Type') != 'x11' or s.get('Active') != 'yes': raise RuntimeError('Есть сеанс, которому невозможно показать предупреждение')
        # notify-send returning success confirms delivery to the user's notification service.
        as_user(s, ['notify-send', '--urgency=critical', '--expire-time=120000', 'Remote Desk', 'Через 120 секунд компьютер будет выключен или перезагружен. Сохраните работу.'], timeout=10)
    for _ in range(120): cancelled(); time.sleep(1)
    if session_key(before) != session_key(sessions()): raise RuntimeError('Состав сеансов изменился; операция отложена')

def grub_info():
    config, defaults = read('/boot/grub/grub.cfg'), read('/etc/default/grub')
    entries = []
    for line in config.splitlines():
        if re.match(r'^\s*menuentry\s', line):
            title = re.search(r"menuentry\s+(['\"])(.*?)\1", line)
            ident = re.search(r"(?:--id|\$menuentry_id_option)\s+(['\"])(.*?)\1", line)
            if title and ident: entries.append((title.group(2), ident.group(2)))
    default_match = re.search(r'^\s*GRUB_DEFAULT\s*=\s*[\"\']?([^\"\'\n#]+)', defaults, re.M)
    default = default_match.group(1).strip() if default_match else '0'
    safe_default = bool(entries) and (default == '0' or default in entries[0]) and 'windows' not in entries[0][0].lower() and ('astra' in entries[0][0].lower() or 'linux' in entries[0][0].lower())
    if re.search(r'^\s*GRUB_SAVEDEFAULT\s*=\s*[\"\']?true', defaults, re.M): safe_default = False
    fs = run(['findmnt', '-n', '-o', 'FSTYPE,SOURCE', '-T', '/boot/grub/grubenv'], check=False).stdout.strip()
    env_result = run(['grub-editenv', '/boot/grub/grubenv', 'list'], check=False) if shutil.which('grub-editenv') else None
    env_values = dict(line.split('=',1) for line in (env_result.stdout.splitlines() if env_result else []) if '=' in line)
    supported_fs = bool(fs) and fs.split()[0] in ('ext2','ext3','ext4','vfat') and not any(x in fs for x in ('/mapper/', '/dev/md'))
    syntax_ok = bool(shutil.which('grub-script-check')) and run(['grub-script-check', '/boot/grub/grub.cfg'], check=False).returncode == 0
    ready = safe_default and supported_fs and syntax_ok and env_result is not None and env_result.returncode == 0 and not env_values.get('next_entry') and bool(shutil.which('grub-reboot'))
    digest = hashlib.sha256((config+'\n'+defaults+'\n'+fs).encode()).hexdigest().upper()
    return dict(GrubReady='yes' if ready else 'no', BootConfigHash=digest, GrubDefault='Astra' if safe_default else 'Не подтвержден', GrubStorage=fs, WindowsMenuIds='\n'.join(ident for title,ident in entries if 'windows' in title.lower()), GrubEntries='\n'.join(title+' → '+ident for title,ident in entries), GrubPilot='Одноразовость требует испытания на тестовом ПК')

def process_data(pid):
    base=pathlib.Path('/proc')/str(pid)
    stat=(base/'stat').read_text(); tail=stat[stat.rfind(')')+2:].split()
    uid=base.stat().st_uid
    return dict(Id=int(pid), Name=(base/'comm').read_text().strip(), Path=str((base/'exe').resolve(strict=True)), User=pwd.getpwuid(uid).pw_name, Session=str(tail[3]), Created=str(tail[19]))

def probe():
    os_release=dict(line.split('=',1) for line in read('/etc/os-release').splitlines() if '=' in line)
    if 'astra' not in os_release.get('ID','').lower() and not pathlib.Path('/etc/astra_version').exists(): raise RuntimeError('SSH отвечает, но ОС не Astra Linux')
    disks=[]
    mounts=run(['findmnt','--json','--real','-o','TARGET,SOURCE,FSTYPE'], check=False)
    def add_mounts(items):
        for m in items:
            target=m.get('target','')
            if m.get('source','').startswith('/dev/'):
                try:
                    stat=os.statvfs(target)
                    disks.append(dict(Name=target,Size=stat.f_blocks*stat.f_frsize,Free=stat.f_bavail*stat.f_frsize,Health='Нет данных SMART'))
                except OSError: pass
            add_mounts(m.get('children',[]))
    if mounts.returncode == 0: add_mounts(json.loads(mounts.stdout).get('filesystems',[]))
    if shutil.which('smartctl'):
        for disk in disks:
            try:
                device=run(['findmnt','-n','-o','SOURCE','-T',disk['Name']]).stdout.strip()
                # Map only a direct disk/partition. Do not report a guessed health for RAID/LVM.
                topology=run(['lsblk','-n','-o','TYPE,PKNAME',device]).stdout.strip().split()
                if topology and topology[0]=='part' and len(topology)==2:device='/dev/'+topology[1]
                elif not topology or topology[0]!='disk':continue
                smart=run(['smartctl','-H','-j',device],check=False)
                data=json.loads(smart.stdout)
                passed=data.get('smart_status',{}).get('passed')
                if isinstance(passed,bool):disk['Health']='SMART: OK' if passed else 'SMART: FAILED'
            except (OSError,RuntimeError,ValueError,KeyError):pass
    services=[]
    for name in re.split(r'[\r\n;]+',options.get('AstraServices','glpi-agent')):
        name=name.strip()
        if not name: continue
        if not re.fullmatch(r'[A-Za-z0-9_.@-]+',name): raise RuntimeError('Некорректное имя службы')
        state=run(['systemctl','is-active',name],check=False).stdout.strip()
        services.append(dict(Name=name,State=state or 'unknown'))
    processes=[]
    for entry in (pathlib.Path('/proc').iterdir() if payload.get('Details') else []):
        if entry.name.isdigit():
            try: processes.append(process_data(entry.name))
            except (OSError,ValueError,KeyError): pass
    diag={'SSH':'Доступен','sudo':'Доступен','python3':sys.version.split()[0], 'AstraVersion':read('/etc/astra_version')}
    for tool in ('x11vnc','notify-send','zenity','apt-get','dpkg-query','loginctl','smartctl','busctl'): diag[tool]=shutil.which(tool) or 'Не установлен'
    try: diag.update(grub_info())
    except Exception as e: diag['GRUB']=str(e)
    active=sessions()
    return dict(Os='Astra',Uuid=read('/sys/class/dmi/id/product_uuid'),Serial=read('/sys/class/dmi/id/product_serial'),HostName=os.uname().nodename,
        Version=os_release.get('PRETTY_NAME','Astra').strip('"')+' '+read('/etc/astra_version'),Architecture={'x86_64':'x64','aarch64':'arm64','i686':'x86'}.get(os.uname().machine,os.uname().machine),
        BootId=read('/proc/sys/kernel/random/boot_id'),User=', '.join(s.get('Name','') for s in active),Reachable=True,State='Управление доступно',
        Disks=disks,Services=services,Processes=processes,Hardware={'Manufacturer':read('/sys/class/dmi/id/sys_vendor'),'Model':read('/sys/class/dmi/id/product_name'),'Memory':next((s for s in read('/proc/meminfo').splitlines() if s.startswith('MemTotal:')),''),'CPU':next((s.split(':',1)[1].strip() for s in read('/proc/cpuinfo').splitlines() if s.startswith('model name')),'')},Diagnostics=diag)

def package_version(name):
    if not re.fullmatch(r'[a-z0-9][a-z0-9+.-]*',name): raise RuntimeError('Некорректное имя пакета')
    result=run(['dpkg-query','-W','-f=${Status}\n${Version}',name],check=False)
    lines=result.stdout.splitlines()
    return lines[-1] if result.returncode == 0 and len(lines)>1 and lines[0]=='install ok installed' else ''

def install():
    pkg=payload['Package']
    if pkg['Os']!='Astra' or pkg['Kind'] not in ('DEB','APT'): raise RuntimeError('Пакет не для Astra')
    architecture={'x86_64':'x64','aarch64':'arm64','i686':'x86'}.get(os.uname().machine,'unknown')
    if pkg['Architecture'] not in ('any',architecture): raise RuntimeError('Архитектура пакета не подходит')
    if package_version(pkg['Detection']) == pkg['Version']: finish('Succeeded','Нужная версия уже установлена'); return
    if pkg['Kind']=='DEB':
        source=DIRECTORY/'package.deb'
        with source.open('rb') as f:
            digest=hashlib.sha256()
            for block in iter(lambda:f.read(1024*1024),b''):digest.update(block)
        if digest.hexdigest().upper()!=pkg['Sha256'].upper(): raise RuntimeError('SHA-256 не совпал')
        package_name=run(['dpkg-deb','-f',str(source),'Package']).stdout.strip()
        package_release=run(['dpkg-deb','-f',str(source),'Version']).stdout.strip()
        package_arch=run(['dpkg-deb','-f',str(source),'Architecture']).stdout.strip()
        native_arch=run(['dpkg','--print-architecture']).stdout.strip()
        if pkg['Detection']!=package_name or pkg['Version']!=package_release or package_arch not in ('all',native_arch): raise RuntimeError('Метаданные DEB не соответствуют каталогу')
        target=str(source)
    else:
        if not re.fullmatch(r'[a-z0-9][a-z0-9+.-]*',pkg['Source']):raise RuntimeError('Некорректное имя APT')
        target=pkg['Source']+'='+pkg['Version']
    cancelled()
    environment=dict(os.environ,DEBIAN_FRONTEND='noninteractive',NEEDRESTART_MODE='l')
    result=run(['apt-get','-y','--no-remove','-o','DPkg::Lock::Timeout=0','-o','DPkg::Options::=--force-confold','install',target],timeout=7000,check=False,env=environment)
    (DIRECTORY/'installer.log').write_text(result.stdout+'\n'+result.stderr)
    if result.returncode:
        busy=any(x in result.stderr.lower() for x in ('could not get lock','unable to acquire','lock is held'))
        finish('Deferred' if busy else 'Failed','Пакетный менеджер занят' if busy else 'APT завершился с кодом '+str(result.returncode)+': '+result.stderr[-500:]);return
    if package_version(pkg['Detection'])!=pkg['Version']:finish('Uncertain','Установка завершена, версия не подтверждена');return
    finish('RebootRequired' if pathlib.Path('/var/run/reboot-required').exists() else 'Succeeded','Версия подтверждена: '+pkg['Version'])

def power():
    action=payload.get('Action')
    if action not in ('Shutdown','RestartAstra','BootWindows'):raise RuntimeError('Неизвестное действие питания')
    warning(); verify_identity(); cancelled()
    chosen=False
    try:
        if action=='BootWindows':
            info=grub_info(); menu=payload.get('WindowsMenuId','')
            if not payload.get('BootPilotVerified') or info['GrubReady']!='yes' or payload.get('BootConfigHash')!=info['BootConfigHash'] or menu not in info['WindowsMenuIds'].splitlines(): raise RuntimeError('Профиль GRUB не проверен или изменился')
            run(['grub-reboot',menu]);chosen=True
            values=dict(x.split('=',1) for x in run(['grub-editenv','/boot/grub/grubenv','list']).stdout.splitlines() if '=' in x)
            if values.get('next_entry')!=menu:raise RuntimeError('GRUB не подтвердил одноразовый выбор')
        cancelled()
        # Read logind's typed inhibitor list; noninteractive root systemctl may otherwise ignore it.
        inhibitors=shlex.split(run(['busctl','--system','call','org.freedesktop.login1','/org/freedesktop/login1','org.freedesktop.login1.Manager','ListInhibitors']).stdout)
        if len(inhibitors)<2 or inhibitors[0]!='a(ssssuu)' or len(inhibitors)!=2+int(inhibitors[1])*6:raise RuntimeError('Не удалось проверить блокировки выключения')
        for offset in range(2,len(inhibitors),6):
            what,who,why,mode=inhibitors[offset:offset+4]
            if 'shutdown' in what.split(':') and mode=='block':raise RuntimeError('Выключение заблокировано приложением: '+who+' — '+why)
        result=run(['systemctl','poweroff' if action=='Shutdown' else 'reboot'],check=False)
        if result.returncode:raise RuntimeError('Система отклонила перезагрузку: '+result.stderr.strip())
        finish('Uncertain','Запрос питания принят; требуется подтверждение загрузки целевой ОС')
    except Exception:
        if chosen:
            values=dict(x.split('=',1) for x in run(['grub-editenv','/boot/grub/grubenv','list'],check=False).stdout.splitlines() if '=' in x)
            if values.get('next_entry')==payload.get('WindowsMenuId'):run(['grub-editenv','/boot/grub/grubenv','unset','next_entry'],check=False)
        raise

def restart_app():
    app,expected=payload['Application'],payload['Process']
    if app['Os']!='Astra' or not str(app['Path']).startswith('/') or app['Path']!=expected['Path']:raise RuntimeError('Приложение не соответствует разрешенному пути')
    current=process_data(int(expected['Id']))
    if any(current[k]!=expected[k] for k in ('Path','Created','User','Session')):raise RuntimeError('Процесс изменился')
    candidates=[s for s in sessions() if s.get('Name')==expected['User'] and s.get('Active')=='yes' and s.get('Type')=='x11']
    if len(candidates)!=1:raise RuntimeError('Не удалось однозначно подтвердить X11-сеанс')
    session=candidates[0]
    answer=as_user(session,['zenity','--question','--default-cancel','--timeout=120','--title=Remote Desk','--text=Разрешить перезапуск приложения? Сохраните работу. Если процесс не завершится за 15 секунд, он будет завершен принудительно; несохраненные данные могут быть потеряны.'],check=False)
    if answer.returncode:finish('Deferred','Пользователь не разрешил перезапуск');return
    cancelled()
    if process_data(int(expected['Id']))!=current:raise RuntimeError('Процесс изменился после согласия')
    os.kill(int(expected['Id']),signal.SIGTERM)
    for _ in range(15):
        if not pathlib.Path('/proc/'+str(expected['Id'])).exists():break
        time.sleep(1)
    if pathlib.Path('/proc/'+str(expected['Id'])).exists():
        if process_data(int(expected['Id']))!=current:raise RuntimeError('PID повторно использован')
        os.kill(int(expected['Id']),signal.SIGKILL)
    if session_key([session])!=session_key([s for s in sessions() if s.get('Id')==session['Id']]):raise RuntimeError('Сеанс изменился; запуск отменен')
    env=session_environment(session)
    subprocess.Popen(['runuser','-u',session['Name'],'--','env']+[k+'='+v for k,v in env.items()]+[app['Path']]+shlex.split(app.get('Arguments','')),stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,start_new_session=True)
    time.sleep(3)
    found=False
    for p in pathlib.Path('/proc').iterdir():
        if p.name.isdigit():
            try:
                item=process_data(p.name)
                if item['Path']==app['Path'] and item['User']==expected['User'] and item['Id']!=expected['Id']:found=True
            except (OSError,ValueError,KeyError):pass
    finish('Succeeded' if found else 'Uncertain','Новый процесс подтвержден' if found else 'Команда запуска отправлена, процесс не подтвержден')

def vnc_start():
    available=[s for s in sessions() if s.get('Active')=='yes' and s.get('Type')=='x11']
    if len(available)!=1:raise RuntimeError('Для помощи нужен один подтвержденный активный X11-сеанс')
    session=available[0];env=session_environment(session)
    auth_file=DIRECTORY/'vnc.pass'; raw=payload.get('PasswordFile','')
    import base64
    data=base64.b64decode(raw,validate=True)
    if len(data)!=8:raise RuntimeError('Некорректный одноразовый ключ VNC')
    auth_file.write_bytes(data);os.chmod(auth_file,0o600)
    # Root reads auth, but connects only to the already verified user's display. No display discovery fallback.
    log=(DIRECTORY/'vnc.log').open('w')
    process=subprocess.Popen(['x11vnc','-norc','-localhost','-display',env['DISPLAY'],'-auth',env['XAUTHORITY'],'-rfbauth',str(auth_file),'-autoport','5900','-once','-accept','popup:120','-timeout','150','-nolookup','-noipv6','-nevershared'],stdin=subprocess.DEVNULL,stdout=log,stderr=log,start_new_session=True)
    created=None
    try:
        for _ in range(60):
            if process.poll() is not None:raise RuntimeError('x11vnc не запустился: '+read(DIRECTORY/'vnc.log')[-600:])
            if created is None:created=process_data(process.pid)['Created']
            match=re.search(r'PORT=(\d+)',read(DIRECTORY/'vnc.log'))
            if match:
                finish('Succeeded','Ожидание согласия пользователя',dict(Port=int(match.group(1)),Pid=process.pid,Created=created,Session=session['Id']))
                break
            time.sleep(0.25)
        else:raise RuntimeError('Порт VNC не получен')
        # Lease renewed by the console. Kill this exact server if the console/tunnel disappears.
        lease=DIRECTORY/'vnc.lease';lease.touch();os.chown(lease,DIRECTORY.stat().st_uid,DIRECTORY.stat().st_gid);os.chmod(lease,0o600); deadline=time.monotonic()+8*3600
        while process.poll() is None:
            if time.monotonic()>deadline or (DIRECTORY/'vnc.stop').exists() or time.time()-lease.stat().st_mtime>45:break
            if not any(s.get('Id')==session['Id'] and s.get('User')==session['User'] and s.get('Active')=='yes' for s in sessions()):break
            time.sleep(3)
    finally:
        if process.poll() is None:process.terminate()
        try:process.wait(timeout=5)
        except subprocess.TimeoutExpired:process.kill();process.wait()
        auth_file.unlink(missing_ok=True)
        request['Payload']={}
        REQUEST.write_text(json.dumps(request),encoding='utf-8')
        log.close()

try:
    if request['Operation']!='Probe':verify_identity()
    operation=request['Operation']
    if operation=='Probe':finish('Succeeded','Состояние Astra получено',probe())
    elif operation=='Install':install()
    elif operation=='Power':power()
    elif operation=='RestartApplication':restart_app()
    elif operation=='VncStart':vnc_start()
    else:raise RuntimeError('Операция не поддерживается в Astra')
except Exception as error:
    finish('Deferred' if request.get('Operation') in ('Power','RestartApplication') else 'Failed',str(error))
