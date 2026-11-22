window.__ModuleLoader__.load({
  id: '@local/listen-together',
  factory(require) {
    const React = require('react');
    const h = React.createElement;
    const { useCallback, useState } = React;

    const NS = 'listen-together';
    const DICT = {
      zh: {
        label: '一起听歌',
        open: '打开悬浮窗',
        close: '收起悬浮窗',
        settings: '一起听歌设置',
        resizeHint: '拖窗口边缘或右下角改大小。',
        opacity: '透明度',
        waveColor: '波形颜色',
        waveOpacity: '波形透明度',
        apply: '应用',
        locked: '鼠标穿透',
        lockedHint: '锁上之后点击会穿过去，只有小锁能点。',
        background: '背景图片',
        choose: '选择图片',
        adjust: '调整位置',
        clear: '清除背景',
        busy: '正在处理…',
        noSession: '先开一个会话，再点这个。',
        failed: '操作失败',
        done: '好了',
      },
      en: {
        label: 'Listen together',
        open: 'Show floating window',
        close: 'Hide floating window',
        settings: 'Listen together settings',
        resizeHint: 'Drag an edge or the bottom-right corner to resize.',
        opacity: 'Opacity',
        waveColor: 'Wave color',
        waveOpacity: 'Wave opacity',
        apply: 'Apply',
        locked: 'Click-through',
        lockedHint: 'When locked, clicks pass through except the small lock.',
        background: 'Background image',
        choose: 'Choose image',
        adjust: 'Adjust position',
        clear: 'Clear',
        busy: 'Working…',
        noSession: 'Open a session first.',
        failed: 'Action failed',
        done: 'Done',
      },
    };

    const css = `
      .lt-root{position:fixed;top:10px;left:250px;z-index:70;pointer-events:auto;-webkit-app-region:no-drag;font-family:var(--dsw-font-family, "Microsoft YaHei UI", system-ui, sans-serif);}
      .lt-fab{width:34px;height:34px;border-radius:50%;display:flex;align-items:center;justify-content:center;cursor:pointer;-webkit-app-region:no-drag;
        background:rgba(18,21,28,.86);border:1px solid rgba(128,140,170,.28);color:#cfd6ea;
        box-shadow:0 6px 20px rgba(0,0,0,.35);transition:transform .15s ease, border-color .15s ease;position:relative;}
      .lt-fab:hover{transform:translateY(-1px);border-color:rgba(150,166,220,.55);}
      .lt-fab.on{color:#a9bcff;}
      .lt-dot{position:absolute;right:1px;bottom:1px;width:7px;height:7px;border-radius:50%;background:#5d6577;border:1px solid rgba(18,21,28,.9);}
      .lt-fab.on .lt-dot{background:#8fa8ff;}
      .lt-gear{position:absolute;right:-4px;bottom:-4px;width:14px;height:14px;border-radius:50%;
        background:#232838;border:1px solid rgba(128,140,170,.3);display:flex;align-items:center;justify-content:center;
        font-size:9px;color:#9aa4bd;opacity:0;transition:opacity .15s ease;cursor:pointer;}
      .lt-root:hover .lt-gear{opacity:1;}
      .lt-panel{position:absolute;top:40px;left:0;width:290px;padding:14px;border-radius:12px;
        background:var(--dsw-alias-bg-elevated, #151821);border:1px solid var(--dsw-alias-border-secondary, #2a3142);
        box-shadow:0 16px 48px rgba(0,0,0,.45);color:var(--dsw-alias-label-primary, #e6e9f2);font-size:12px;}
      .lt-title{font-size:13px;font-weight:600;margin-bottom:10px;}
      .lt-row{display:flex;align-items:center;justify-content:space-between;gap:8px;margin:8px 0;}
      .lt-row label{color:var(--dsw-alias-label-secondary, #a8afc2);}
      .lt-range{flex:1;min-width:80px;accent-color:#8fa8ff;}
      .lt-val{width:44px;text-align:right;color:var(--dsw-alias-label-tertiary, #8b93a8);}
      .lt-btn{border:1px solid var(--dsw-alias-border-secondary, #2a3142);background:rgba(143,168,255,.08);
        color:var(--dsw-alias-label-primary, #e6e9f2);border-radius:8px;padding:6px 10px;font-size:12px;cursor:pointer;}
      .lt-btn:hover{background:rgba(143,168,255,.16);}
      .lt-btn.primary{background:rgba(143,168,255,.22);border-color:rgba(143,168,255,.35);}
      .lt-sub{color:var(--dsw-alias-label-tertiary, #8b93a8);font-size:11px;line-height:1.5;margin-top:2px;}
      .lt-error{color:#ff9b9b;font-size:11px;margin-top:8px;white-space:pre-wrap;}
    `;

    function icon() {
      return h('svg', { width: 16, height: 16, viewBox: '0 0 16 16', fill: 'none' },
        h('path', { d: 'M2.5 9.5V7a5.5 5.5 0 0 1 11 0v2.5', stroke: 'currentColor', strokeWidth: 1.4, strokeLinecap: 'round' }),
        h('rect', { x: 1.6, y: 8.6, width: 3.2, height: 4.8, rx: 1.4, fill: 'currentColor', opacity: .9 }),
        h('rect', { x: 11.2, y: 8.6, width: 3.2, height: 4.8, rx: 1.4, fill: 'currentColor', opacity: .9 }));
    }

    function Panel(props) {
      const { t, state, onClose, onAction, busy, error } = props;
      const settings = state.settings;
      const [opacity, setOpacity] = useState(Math.round(settings.opacity * 100));
      const [waveColor, setWaveColor] = useState(settings.waveColor || '#8fa8ff');
      const [waveOpacity, setWaveOpacity] = useState(settings.waveOpacity ?? 78);

      const commitOpacity = () => onAction({ action: 'opacity', value: opacity / 100 });
      const commitWave = () => onAction({ action: 'wave', color: waveColor, opacity: waveOpacity });

      return h('div', { className: 'lt-panel' },
        h('div', { className: 'lt-title' }, t('settings')),
        h('div', { className: 'lt-sub', style: { marginBottom: '8px' } }, t('resizeHint')),
        h('div', { className: 'lt-row' },
          h('label', null, t('opacity')),
          h('input', {
            className: 'lt-range', type: 'range', min: 25, max: 100, step: 1, value: opacity,
            onChange: (e) => setOpacity(Number(e.target.value)), onMouseUp: commitOpacity, onTouchEnd: commitOpacity,
          }),
          h('span', { className: 'lt-val' }, `${opacity}%`)),
        h('div', { className: 'lt-row' },
          h('label', null, t('waveColor')),
          h('div', { style: { display: 'flex', alignItems: 'center', gap: '6px' } },
            h('input', {
              type: 'color', value: waveColor,
              onChange: (e) => setWaveColor(e.target.value),
              style: { width: '34px', height: '24px', padding: 0, border: '1px solid var(--dsw-alias-border-secondary, #2a3142)', borderRadius: '6px', background: 'transparent', cursor: 'pointer' },
            }),
            h('button', {
              className: 'lt-btn', type: 'button', disabled: busy,
              onClick: commitWave,
            }, t('apply')))),
        h('div', { className: 'lt-row' },
          h('label', null, t('waveOpacity')),
          h('input', {
            className: 'lt-range', type: 'range', min: 0, max: 100, step: 1, value: waveOpacity,
            onChange: (e) => setWaveOpacity(Number(e.target.value)), onMouseUp: commitWave, onTouchEnd: commitWave,
          }),
          h('span', { className: 'lt-val' }, `${waveOpacity}%`)),
        h('div', { className: 'lt-row' },
          h('div', null,
            h('label', null, t('locked')),
            h('div', { className: 'lt-sub' }, t('lockedHint'))),
          h('button', {
            className: 'lt-btn' + (settings.locked ? ' primary' : ''), type: 'button', disabled: busy,
            onClick: () => onAction({ action: 'lock', value: !settings.locked }),
          }, settings.locked ? 'On' : 'Off')),
        h('div', { className: 'lt-row' },
          h('label', null, t('background')),
          h('div', null,
            h('button', {
              className: 'lt-btn', type: 'button', disabled: busy,
              onClick: () => onAction({ action: 'background-pick' }),
            }, busy ? t('busy') : t('choose')),
            h('button', {
              className: 'lt-btn', type: 'button', style: { marginLeft: 6 }, disabled: busy,
              onClick: () => onAction({ action: 'background-edit' }),
            }, t('adjust')),
            settings.background
              ? h('button', {
                  className: 'lt-btn', type: 'button', style: { marginLeft: 6 }, disabled: busy,
                  onClick: () => onAction({ action: 'background-clear' }),
                }, t('clear'))
              : null)),
        h('div', { className: 'lt-row' },
          h('button', {
            className: 'lt-btn primary', type: 'button', disabled: busy,
            onClick: () => onAction({ action: state.visible ? 'hide' : 'show' }),
          }, state.visible ? t('close') : t('open')),
          h('button', { className: 'lt-btn', type: 'button', onClick: onClose }, t('done'))),
        error ? h('div', { className: 'lt-error' }, error) : null,
      );
    }

    function Overlay(props) {
      const { t, useSessions, execute } = props;
      const sessionId = useSessions((state) => state.ids[0]);
      const [state, setState] = useState({
        visible: true,
        settings: { width: 356, height: 116, opacity: 0.97, locked: false, background: false, waveColor: '#8fa8ff', waveOpacity: 78 },
      });
      const [open, setOpen] = useState(false);
      const [busy, setBusy] = useState(false);
      const [error, setError] = useState('');

      const run = useCallback(async (line) => {
        if (!sessionId) throw new Error(t('noSession'));
        setBusy(true);
        try {
          const result = await execute(sessionId, line);
          if (!result) throw new Error(t('failed'));
          if (result.kind === 'error') throw new Error(result.text);
          return result;
        } finally {
          setBusy(false);
        }
      }, [sessionId, execute, t]);

      const onAction = useCallback(async (body) => {
        const action = body.action;
        let line;
        if (action === 'toggle') line = '/listen-together toggle';
        else if (action === 'show' || action === 'hide') line = `/listen-together ${action}`;
        else if (action === 'lock') line = `/listen-together lock ${body.value ? 'on' : 'off'}`;
        else if (action === 'size') line = `/listen-together size ${body.width} ${body.height}`;
        else if (action === 'opacity') line = `/listen-together opacity ${body.value}`;
        else if (action === 'wave') line = `/listen-together wave ${body.color} ${body.opacity}`;
        else if (action === 'background-pick') line = '/listen-together background-pick';
        else if (action === 'background-edit') line = '/listen-together background-edit';
        else if (action === 'background-clear') line = '/listen-together background-clear';
        else line = '/listen-together toggle';
        try {
          await run(line);
          setState((prev) => {
            const settings = { ...prev.settings };
            if (action === 'size') { settings.width = body.width; settings.height = body.height; }
            if (action === 'opacity') settings.opacity = body.value;
            if (action === 'lock') settings.locked = body.value;
            if (action === 'wave') { settings.waveColor = body.color; settings.waveOpacity = body.opacity; }
            if (action === 'background-pick') settings.background = true;
            if (action === 'background-clear') settings.background = false;
            const visible = action === 'show' ? true : action === 'hide' ? false
              : action === 'toggle' ? !prev.visible : prev.visible;
            return { visible, settings };
          });
          setError('');
          return true;
        } catch (cause) {
          setError(cause && cause.message ? cause.message : t('failed'));
          return false;
        }
      }, [run, t]);

      const onFab = async () => {
        const ok = await onAction({ action: 'toggle' });
        if (!ok) setOpen(true);
      };

      return h('div', { className: 'lt-root' },
        h('style', null, css),
        h('div', {
          className: 'lt-fab' + (state.visible ? ' on' : ''),
          title: error || (state.visible ? t('close') : t('open')),
          onClick: onFab,
        }, icon(), h('span', { className: 'lt-dot' }),
          h('span', {
            className: 'lt-gear', title: t('settings'),
            onClick: (event) => { event.stopPropagation(); setOpen((value) => !value); },
          }, '⚙')),
        open ? h(Panel, {
          t, state, busy, error,
          onClose: () => setOpen(false),
          onAction,
        }) : null,
      );
    }

    return {
      inject: ['slots', 'locale', 'remote', 'remote.commands'],
      apply(ctx) {
        ctx.effect(() => {
          const offZh = ctx.locale.register(NS, 'zh', DICT.zh);
          const offEn = ctx.locale.register(NS, 'en', DICT.en);
          return () => { try { offZh(); } catch { /* 忽略 */ } try { offEn(); } catch { /* 忽略 */ } };
        }, 'listen-together: locale');
        const t = ctx.locale.bind(NS);
        const execute = (sessionId, line) => ctx.remote.commands.execute(sessionId, line, []);
        ctx.slots.inject('shell.overlay', () => ctx.slots.register({
          name: 'shell.overlay',
          id: 'listen-together-toggle',
          order: 40,
        }, (props) => h(Overlay, { ...props, t, execute })));
      },
    };
  },
});
