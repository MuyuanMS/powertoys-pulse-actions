const size = document.getElementById('sizing-case');
const popupFrame = document.getElementById('popup-frame');
function measure() {
  const page = popupFrame.contentDocument;
  const main = page?.getElementById('task-content');
  const footer = page?.getElementById('task-popup-footer');
  if (!main || !footer) return;
  const body = page.body.getBoundingClientRect();
  const metrics = {
    viewportWidth: page.documentElement.clientWidth,
    viewportHeight: page.documentElement.clientHeight,
    preferredWidth: body.width,
    preferredHeight: body.height,
    mainHeight: main.clientHeight,
    mainScrollHeight: main.scrollHeight,
    footerBottom: footer.getBoundingClientRect().bottom,
  };
  const errors = [];
  if (Math.abs(body.width - 480) > 1) errors.push('Popup lost its intrinsic 480px width.');
  if (body.height < 359 || body.height > 601) errors.push('Popup height is outside its 360–600px bounds.');
  if (main.clientHeight <= 0) errors.push('Task content has no usable height.');
  if (footer.getBoundingClientRect().bottom > body.bottom + 1) errors.push('Footer is outside the popup body.');
  if (page.getElementById('task-topbar')) errors.push('Popup did not finish initialising.');
  document.getElementById('sizing-result').textContent = errors.length ? 'FAIL: ' + errors.join(' ') : 'PASS: intrinsic popup size is independent of the initial viewport.';
  document.getElementById('sizing-metrics').textContent = JSON.stringify(metrics, null, 2);
}
popupFrame.addEventListener('load', () => requestAnimationFrame(measure));
if (popupFrame.contentDocument?.readyState === 'complete') requestAnimationFrame(measure);
size.addEventListener('change', () => {
  document.getElementById('sizing-result').textContent = 'Waiting for popup layout…';
  document.getElementById('sizing-metrics').textContent = '';
  const [width, height] = size.value.split('x');
  popupFrame.width = width;
  popupFrame.height = height;
  popupFrame.src = 'popup.html?sizing=native-popup';
});
