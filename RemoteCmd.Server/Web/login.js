'use strict';

document.getElementById('loginForm').addEventListener('submit', async (e) => {
  e.preventDefault();
  const err = document.getElementById('err');
  err.textContent = '';
  const res = await fetch('/api/admin/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-RemoteCmd': '1' },
    body: JSON.stringify({ password: document.getElementById('pw').value }),
  });
  if (res.ok) { location.href = '/'; return; }
  err.textContent = res.status === 429 ? 'Too many attempts — wait a few minutes.' : 'Wrong password.';
});
