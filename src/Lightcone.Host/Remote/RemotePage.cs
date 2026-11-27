namespace Lightcone.Host.Remote;

/// <summary>The phone remote's one page (no outside files, works offline on the LAN).</summary>
internal static class RemotePage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no">
<meta name="theme-color" content="#0b0d12">
<title>Lightcone remote</title>
<style>
:root{--bg:#0b0d12;--card:#151924;--line:#262c3b;--text:#e9ecf3;--dim:#8a93a6;--accent:#ffb84d}
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent}
html,body{margin:0;background:var(--bg);color:var(--text);font:16px/1.4 system-ui,-apple-system,Segoe UI,Roboto,sans-serif;user-select:none;-webkit-user-select:none}
main{max-width:420px;margin:0 auto;padding:16px 16px 28px;display:flex;flex-direction:column;gap:16px;min-height:100vh}
header{display:flex;align-items:center;gap:10px}
.cone{width:22px;height:22px;background:conic-gradient(from 200deg,transparent 0 40%,var(--accent) 40% 60%,transparent 60%);border-radius:50%}
h1{font-size:17px;margin:0;font-weight:600;letter-spacing:.02em}
.now{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:14px}
.now .t{font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.now .s{color:var(--dim);font-size:14px;min-height:1.4em}
.bar{height:4px;background:var(--line);border-radius:2px;margin-top:10px;overflow:hidden}
.bar i{display:block;height:100%;width:0;background:var(--accent)}
.pad{position:relative;width:min(78vw,300px);aspect-ratio:1;margin:4px auto;border-radius:50%;background:var(--card);border:1px solid var(--line)}
.pad button{position:absolute;background:none;border:0;color:var(--text);font-size:26px}
.pad .u{top:0;left:33%;width:34%;height:33%}.pad .d{bottom:0;left:33%;width:34%;height:33%}
.pad .l{left:0;top:33%;width:33%;height:34%}.pad .r{right:0;top:33%;width:33%;height:34%}
.pad .ok{left:31%;top:31%;width:38%;height:38%;border-radius:50%;background:var(--line);font-size:17px;font-weight:600}
.row{display:grid;grid-template-columns:repeat(4,1fr);gap:10px}
.row button,.wide{height:54px;border-radius:12px;border:1px solid var(--line);background:var(--card);color:var(--text);font-size:20px}
.row button small{display:block;font-size:11px;color:var(--dim)}
button:active{background:var(--line)!important;transform:scale(.97)}
.play{background:var(--accent)!important;color:#111!important;border:0!important}
.pin{display:none;flex-direction:column;gap:12px;margin-top:20vh;text-align:center}
.pin input{font-size:32px;letter-spacing:.4em;text-align:center;padding:12px;border-radius:12px;border:1px solid var(--line);background:var(--card);color:var(--text)}
.pin p{color:var(--dim);margin:0}
.err{color:#ff7a7a;min-height:1.4em}
body.locked .ui{display:none}body.locked .pin{display:flex}
</style>
</head>
<body class="locked">
<main>
<header><div class="cone"></div><h1>Lightcone</h1></header>
<div class="pin">
<p>Enter the PIN shown in Lightcone under Settings → Phone remote.</p>
<input id="pin" inputmode="numeric" maxlength="4" autocomplete="off">
<button class="wide play" onclick="savePin()">Connect</button>
<div class="err" id="err"></div>
</div>
<section class="now ui"><div class="t" id="t">Connecting…</div><div class="s" id="s"></div><div class="bar"><i id="bar"></i></div></section>
<div class="pad ui">
<button class="u" data-c="up">▲</button><button class="d" data-c="down">▼</button>
<button class="l" data-c="left">◀</button><button class="r" data-c="right">▶</button>
<button class="ok" data-c="accept">OK</button>
</div>
<div class="row ui">
<button data-c="back">↩<small>Back</small></button><button data-c="home">⌂<small>Home</small></button>
<button data-c="options">☰<small>Options</small></button><button data-c="menu">⋯<small>Menu</small></button>
</div>
<div class="row ui">
<button data-c="skipback">⟲<small>Back</small></button><button data-c="playpause" class="play" id="pp">⏯</button>
<button data-c="skipforward">⟳<small>Ahead</small></button><button data-c="next">⏭<small>Next</small></button>
</div>
<div class="row ui">
<button data-c="tabprev">‹<small>Tab</small></button><button data-c="voldown">−<small>Vol</small></button>
<button data-c="volup">+<small>Vol</small></button><button data-c="tabnext">›<small>Tab</small></button>
</div>
</main>
<script>
let pin='';try{pin=localStorage.getItem('lc-pin')||''}catch(e){}
const $=id=>document.getElementById(id);
function savePin(){pin=$('pin').value.trim();try{localStorage.setItem('lc-pin',pin)}catch(e){}poll()}
function lock(msg){document.body.classList.add('locked');$('err').textContent=msg||''}
async function api(path,opt){const r=await fetch(path,Object.assign({headers:{'X-Lightcone-Pin':pin}},opt||{}));if(r.status==401){lock(pin?'Wrong PIN.':'');throw 0}if(r.status==429){lock('Too many tries. Wait half a minute.');throw 0}return r.json()}
function fmt(s){s=Math.max(0,Math.floor(s));const h=Math.floor(s/3600),m=Math.floor(s%3600/60),x=String(s%60).padStart(2,'0');return h?h+':'+String(m).padStart(2,'0')+':'+x:m+':'+x}
async function poll(){if(!pin){lock('');return}try{const st=await api('/api/state');document.body.classList.remove('locked');
$('t').textContent=st.title||st.screen||'Lightcone';$('s').textContent=st.playing?(st.subtitle?st.subtitle+'  ·  ':'')+fmt(st.position)+' / '+fmt(st.duration)+(st.paused?'  ·  paused':''):(st.subtitle||'');
$('bar').style.width=st.playing&&st.duration>0?(100*st.position/st.duration)+'%':'0'}catch(e){if(e!==0)$('t').textContent='Lightcone isn\'t answering…'}}
document.querySelectorAll('[data-c]').forEach(b=>b.addEventListener('click',async()=>{if(navigator.vibrate)navigator.vibrate(12);try{await api('/api/cmd?c='+b.dataset.c,{method:'POST'});setTimeout(poll,250)}catch(e){}}));
$('pin').addEventListener('keydown',e=>{if(e.key==='Enter')savePin()});
poll();setInterval(()=>{if(!document.hidden)poll()},1500);
</script>
</body>
</html>
""";
}
