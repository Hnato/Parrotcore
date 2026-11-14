async function checkServerStatus() {
    const dbText = document.getElementById('dbInfoText');
    const out = document.getElementById('outputPanel');
    try {
        const res = await fetch('/api/status');
        const st = await res.json();
        if (st.databaseEnabled) {
            dbText.textContent = st.databaseConnected
                ? 'Connected to ' + st.databaseProvider + ' database.'
                : 'Database (' + st.databaseProvider + ') is configured but currently offline.';
        } else {
            dbText.textContent = 'Database integration is disabled in settings.';
        }
        out.textContent = 'Server status:\n' + JSON.stringify({
            status: st.status,
            port: st.port,
            bindMode: st.bindMode,
            databaseEnabled: st.databaseEnabled,
            databaseProvider: st.databaseProvider,
            databaseConnected: st.databaseConnected
        }, null, 2);
    } catch (e) {
        out.textContent = 'Could not reach server status endpoint: ' + e.message;
    }
}

checkServerStatus();
