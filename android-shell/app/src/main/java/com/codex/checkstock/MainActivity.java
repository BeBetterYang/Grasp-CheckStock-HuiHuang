package com.codex.checkstock;

import android.Manifest;
import android.annotation.SuppressLint;
import android.app.Activity;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.Rect;
import android.hardware.Camera;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.View;
import android.view.ViewGroup;
import android.webkit.PermissionRequest;
import android.webkit.JavascriptInterface;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;
import android.widget.Toast;

import com.google.zxing.BinaryBitmap;
import com.google.zxing.BarcodeFormat;
import com.google.zxing.DecodeHintType;
import com.google.zxing.LuminanceSource;
import com.google.zxing.MultiFormatReader;
import com.google.zxing.PlanarYUVLuminanceSource;
import com.google.zxing.Result;
import com.google.zxing.common.GlobalHistogramBinarizer;
import com.google.zxing.common.HybridBinarizer;
import com.google.zxing.integration.android.IntentIntegrator;
import com.google.zxing.integration.android.IntentResult;

import java.util.Collection;
import java.util.EnumMap;
import java.util.EnumSet;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;

public class MainActivity extends Activity {
    private static final String PREFS = "codex_check_stock";
    private static final String KEY_SERVER_URL = "server_url";
    private static final int REQ_WEB_CAMERA = 1001;
    private static final int REQ_SCAN_CAMERA = 1002;
    private static final int SCAN_MODE_SERVER = 1;
    private static final int SCAN_MODE_WEB = 2;
    private static final String ACTION_YODEX_SCAN = "com.yodex.SCAN";

    private WebView webView;
    private PermissionRequest pendingPermissionRequest;
    private SharedPreferences prefs;
    private EditText serverInput;
    private Camera scannerCamera;
    private FrameLayout scannerPanel;
    private SurfaceView scannerSurface;
    private Camera.Size scannerPreviewSize;
    private boolean scannerActive;
    private boolean decodingFrame;
    private boolean webManualInputActive;
    private long lastAutoFocusAt;
    private int scannerMode = SCAN_MODE_SERVER;
    private final MultiFormatReader qrReader = new MultiFormatReader();
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private final StringBuilder hardwareScanBuffer = new StringBuilder();
    private final Runnable hardwareScanTimeout = this::flushHardwareScanBuffer;
    private final BroadcastReceiver scanReceiver = new BroadcastReceiver() {
        @Override
        public void onReceive(Context context, Intent intent) {
            String value = extractScanData(intent);
            if (value != null && value.trim().length() >= 3) {
                dispatchHardwareScanResult(value.trim());
            }
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        prefs = getSharedPreferences(PREFS, MODE_PRIVATE);
        registerScanReceiver();

        Map<DecodeHintType, Object> hints = new EnumMap<>(DecodeHintType.class);
        hints.put(DecodeHintType.TRY_HARDER, Boolean.TRUE);
        hints.put(DecodeHintType.ALSO_INVERTED, Boolean.TRUE);
        Collection<BarcodeFormat> formats = EnumSet.of(
                BarcodeFormat.QR_CODE,
                BarcodeFormat.CODE_128,
                BarcodeFormat.CODE_39,
                BarcodeFormat.CODE_93,
                BarcodeFormat.EAN_13,
                BarcodeFormat.EAN_8,
                BarcodeFormat.UPC_A,
                BarcodeFormat.UPC_E,
                BarcodeFormat.ITF,
                BarcodeFormat.CODABAR
        );
        hints.put(DecodeHintType.POSSIBLE_FORMATS, formats);
        qrReader.setHints(hints);

        String savedUrl = prefs.getString(KEY_SERVER_URL, BuildConfig.DEFAULT_SERVER_URL);
        if (savedUrl == null || savedUrl.trim().isEmpty()) {
            showServerSetup("");
        } else {
            openWeb(normalizeServerUrl(savedUrl));
        }
    }

    private void showServerSetup(String initialValue) {
        stopScanner();

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setGravity(Gravity.CENTER_HORIZONTAL);
        root.setPadding(dp(24), dp(42), dp(24), dp(24));
        root.setBackgroundColor(Color.rgb(245, 247, 250));

        TextView title = new TextView(this);
        title.setText("欢迎使用");
        title.setTextSize(26);
        title.setTextColor(Color.rgb(23, 32, 51));
        title.setGravity(Gravity.CENTER);
        title.setTypeface(null, 1);
        root.addView(title, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        serverInput = new EditText(this);
        serverInput.setSingleLine(true);
        serverInput.setText(initialValue == null || initialValue.isEmpty() ? "http://" : initialValue);
        serverInput.setTextSize(16);
        serverInput.setSelectAllOnFocus(false);
        serverInput.setPadding(dp(14), 0, dp(14), 0);
        LinearLayout.LayoutParams inputParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(48));
        inputParams.setMargins(0, dp(32), 0, 0);
        root.addView(serverInput, inputParams);

        Button enterButton = primaryButton("进入");
        enterButton.setOnClickListener(v -> saveAndOpen(serverInput.getText().toString()));
        LinearLayout.LayoutParams enterParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(48));
        enterParams.setMargins(0, dp(18), 0, 0);
        root.addView(enterButton, enterParams);

        Button scanButton = secondaryButton("扫码");
        scanButton.setOnClickListener(v -> startServerScanner());
        LinearLayout.LayoutParams scanParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(46));
        scanParams.setMargins(0, dp(12), 0, 0);
        root.addView(scanButton, scanParams);

        String savedUrl = prefs.getString(KEY_SERVER_URL, "");
        if (savedUrl != null && !savedUrl.isEmpty()) {
            Button clearButton = secondaryButton("清除");
            clearButton.setOnClickListener(v -> {
                prefs.edit().remove(KEY_SERVER_URL).apply();
                serverInput.setText("http://");
                Toast.makeText(this, "已清除", Toast.LENGTH_SHORT).show();
            });
            LinearLayout.LayoutParams clearParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(46));
            clearParams.setMargins(0, dp(12), 0, 0);
            root.addView(clearButton, clearParams);
        }

        setContentView(root);
        serverInput.requestFocus();
    }

    private Button primaryButton(String text) {
        Button button = new Button(this);
        button.setAllCaps(false);
        button.setText(text);
        button.setTextSize(16);
        button.setTextColor(Color.WHITE);
        button.setBackgroundColor(Color.rgb(22, 119, 255));
        return button;
    }

    private Button secondaryButton(String text) {
        Button button = new Button(this);
        button.setAllCaps(false);
        button.setText(text);
        button.setTextSize(16);
        button.setTextColor(Color.rgb(22, 119, 255));
        button.setBackgroundColor(Color.WHITE);
        return button;
    }

    private void saveAndOpen(String value) {
        String url;
        try {
            url = normalizeServerUrl(value);
        } catch (IllegalArgumentException ex) {
            Toast.makeText(this, ex.getMessage(), Toast.LENGTH_LONG).show();
            return;
        }
        prefs.edit().putString(KEY_SERVER_URL, url).apply();
        openWeb(url);
    }

    private String normalizeServerUrl(String raw) {
        String value = raw == null ? "" : raw.trim();
        if (value.isEmpty() || value.equals("http://") || value.equals("https://")) {
            throw new IllegalArgumentException("请输入服务器地址");
        }
        if (!value.startsWith("http://") && !value.startsWith("https://")) {
            value = "http://" + value;
        }
        Uri uri = Uri.parse(value);
        if (uri.getHost() == null || uri.getHost().trim().isEmpty()) {
            throw new IllegalArgumentException("服务器地址格式不正确");
        }
        if (!value.endsWith("/")) value += "/";
        return value;
    }

    @SuppressLint("SetJavaScriptEnabled")
    private void openWeb(String url) {
        stopScanner();
        webView = new WebView(this);
        setContentView(webView);

        WebSettings settings = webView.getSettings();
        settings.setJavaScriptEnabled(true);
        settings.setDomStorageEnabled(true);
        settings.setDatabaseEnabled(true);
        settings.setLoadWithOverviewMode(true);
        settings.setUseWideViewPort(true);
        settings.setMixedContentMode(WebSettings.MIXED_CONTENT_ALWAYS_ALLOW);

        webView.setWebViewClient(new WebViewClient() {
            @Override
            public void onReceivedError(WebView view, WebResourceRequest request, WebResourceError error) {
                super.onReceivedError(view, request, error);
                if (request != null && request.isForMainFrame()) {
                    Toast.makeText(MainActivity.this, "无法连接服务器，请检查地址", Toast.LENGTH_LONG).show();
                    showServerSetup(url);
                }
            }
        });
        webView.setWebChromeClient(new WebChromeClient() {
            @Override
            public void onPermissionRequest(PermissionRequest request) {
                pendingPermissionRequest = request;
                if (checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) {
                    request.grant(request.getResources());
                } else {
                    requestPermissions(new String[] { Manifest.permission.CAMERA }, REQ_WEB_CAMERA);
                }
            }
        });
        webView.addJavascriptInterface(new NativeBridge(), "YodexNative");
        webView.loadUrl(url);
    }

    private void startServerScanner() {
        launchJourneyAppsScanner(SCAN_MODE_SERVER);
    }

    private void startWebScanner() {
        launchJourneyAppsScanner(SCAN_MODE_WEB);
    }

    private void launchJourneyAppsScanner(int mode) {
        scannerMode = mode;
        scannerActive = true;
        IntentIntegrator integrator = new IntentIntegrator(this);
        integrator.setCaptureActivity(PortraitCaptureActivity.class);
        integrator.setDesiredBarcodeFormats(IntentIntegrator.ALL_CODE_TYPES);
        integrator.setPrompt("请将条码或二维码放入框内");
        integrator.setCameraId(0);
        integrator.setBeepEnabled(false);
        integrator.setBarcodeImageEnabled(false);
        integrator.setOrientationLocked(true);
        integrator.initiateScan();
    }

    private void showScannerViewV2() {
        stopScanner();
        scannerActive = true;

        View.OnClickListener cancelScan = v -> {
            if (scannerMode == SCAN_MODE_WEB && webView != null) {
                stopScanner();
                setContentView(webView);
            } else {
                showServerSetup(serverInput == null ? "" : serverInput.getText().toString());
            }
        };

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(Color.WHITE);

        FrameLayout nav = new FrameLayout(this);
        nav.setBackgroundColor(Color.rgb(22, 119, 255));

        TextView back = new TextView(this);
        back.setText("\u2039");
        back.setTextColor(Color.WHITE);
        back.setTextSize(34);
        back.setGravity(Gravity.CENTER);
        back.setOnClickListener(cancelScan);
        nav.addView(back, new FrameLayout.LayoutParams(dp(56), ViewGroup.LayoutParams.MATCH_PARENT, Gravity.LEFT | Gravity.CENTER_VERTICAL));

        TextView title = new TextView(this);
        title.setText(scannerMode == SCAN_MODE_SERVER ? "\u626b\u7801\u8fde\u63a5" : "\u6761\u7801\u5f55\u5165");
        title.setTextColor(Color.WHITE);
        title.setTextSize(18);
        title.setGravity(Gravity.CENTER);
        title.setTypeface(null, 1);
        nav.addView(title, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        root.addView(nav, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(45)));

        scannerPanel = new FrameLayout(this);
        scannerPanel.setBackgroundColor(Color.BLACK);
        scannerSurface = new SurfaceView(this);
        scannerSurface.setOnClickListener(v -> triggerAutoFocus());
        scannerPanel.addView(scannerSurface, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT, Gravity.CENTER));
        scannerPanel.addView(new ScannerOverlayView(this), new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        root.addView(scannerPanel, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(250)));

        View blank = new View(this);
        blank.setBackgroundColor(Color.WHITE);
        root.addView(blank, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1));

        setContentView(root);
        scannerSurface.getHolder().addCallback(new SurfaceHolder.Callback() {
            @Override
            public void surfaceCreated(SurfaceHolder holder) {
                openScannerCamera(holder);
            }

            @Override
            public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) {
                adjustScannerSurfaceLayout();
            }

            @Override
            public void surfaceDestroyed(SurfaceHolder holder) {
                stopScanner();
            }
        });
    }

    private void showScannerView() {
        stopScanner();
        scannerActive = true;

        FrameLayout root = new FrameLayout(this);
        scannerSurface = new SurfaceView(this);
        scannerSurface.setOnClickListener(v -> triggerAutoFocus());
        root.addView(scannerSurface, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        TextView tip = new TextView(this);
        tip.setText(scannerMode == SCAN_MODE_SERVER ? "请扫描服务器地址二维码" : "请扫描条码或二维码");
        tip.setTextColor(Color.WHITE);
        tip.setTextSize(18);
        tip.setGravity(Gravity.CENTER);
        tip.setBackgroundColor(Color.argb(150, 0, 0, 0));
        FrameLayout.LayoutParams tipParams = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(58), Gravity.TOP);
        root.addView(tip, tipParams);

        Button cancel = secondaryButton("取消");
        cancel.setOnClickListener(v -> {
            if (scannerMode == SCAN_MODE_WEB && webView != null) {
                stopScanner();
                setContentView(webView);
            } else {
                showServerSetup(serverInput == null ? "" : serverInput.getText().toString());
            }
        });
        FrameLayout.LayoutParams cancelParams = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(52), Gravity.BOTTOM);
        cancelParams.setMargins(dp(20), 0, dp(20), dp(24));
        root.addView(cancel, cancelParams);

        setContentView(root);
        scannerSurface.getHolder().addCallback(new SurfaceHolder.Callback() {
            @Override
            public void surfaceCreated(SurfaceHolder holder) {
                openScannerCamera(holder);
            }

            @Override
            public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) {
            }

            @Override
            public void surfaceDestroyed(SurfaceHolder holder) {
                stopScanner();
            }
        });
    }

    private void openScannerCamera(SurfaceHolder holder) {
        try {
            scannerCamera = Camera.open();
            configureScannerCamera(scannerCamera);
            scannerCamera.setDisplayOrientation(90);
            scannerCamera.setPreviewDisplay(holder);
            scannerCamera.setPreviewCallback(this::decodePreviewFrame);
            scannerCamera.startPreview();
            adjustScannerSurfaceLayout();
            triggerAutoFocus();
        } catch (Exception ex) {
            Toast.makeText(this, "无法打开摄像头：" + ex.getMessage(), Toast.LENGTH_LONG).show();
            showServerSetup("");
        }
    }

    private void configureScannerCamera(Camera camera) {
        Camera.Parameters params = camera.getParameters();

        Camera.Size previewSize = choosePreviewSize(params.getSupportedPreviewSizes());
        if (previewSize != null) {
            params.setPreviewSize(previewSize.width, previewSize.height);
            scannerPreviewSize = previewSize;
        }

        List<String> focusModes = params.getSupportedFocusModes();
        if (focusModes != null) {
            if (focusModes.contains(Camera.Parameters.FOCUS_MODE_CONTINUOUS_VIDEO)) {
                params.setFocusMode(Camera.Parameters.FOCUS_MODE_CONTINUOUS_VIDEO);
            } else if (focusModes.contains(Camera.Parameters.FOCUS_MODE_CONTINUOUS_PICTURE)) {
                params.setFocusMode(Camera.Parameters.FOCUS_MODE_CONTINUOUS_PICTURE);
            } else if (focusModes.contains(Camera.Parameters.FOCUS_MODE_AUTO)) {
                params.setFocusMode(Camera.Parameters.FOCUS_MODE_AUTO);
            }
        }
        configureFocusAndMetering(params);
        params.setRecordingHint(true);

        camera.setParameters(params);
    }

    private void configureFocusAndMetering(Camera.Parameters params) {
        Rect centerBand = new Rect(-850, -220, 850, 220);
        ArrayList<Camera.Area> areas = new ArrayList<>();
        areas.add(new Camera.Area(centerBand, 1000));
        try {
            if (params.getMaxNumFocusAreas() > 0) {
                params.setFocusAreas(areas);
            }
            if (params.getMaxNumMeteringAreas() > 0) {
                params.setMeteringAreas(areas);
            }
        } catch (Exception ignored) {
        }
    }

    private Camera.Size choosePreviewSize(List<Camera.Size> sizes) {
        if (sizes == null || sizes.isEmpty()) return null;
        Camera.Size best = null;
        for (Camera.Size size : sizes) {
            if (size.width < 1280 || size.height < 720) continue;
            double ratio = (double) size.width / (double) size.height;
            if (Math.abs(ratio - (16.0 / 9.0)) > 0.25) continue;
            int area = size.width * size.height;
            int bestArea = best == null ? 0 : best.width * best.height;
            if (area <= 1920 * 1080 && area > bestArea) {
                best = size;
            }
        }
        if (best != null) return best;

        for (Camera.Size size : sizes) {
            if (best == null || size.width * size.height > best.width * best.height) {
                best = size;
            }
        }
        return best;
    }

    private void adjustScannerSurfaceLayout() {
        if (scannerPanel == null || scannerSurface == null || scannerPreviewSize == null) return;
        scannerPanel.post(() -> {
            int panelWidth = scannerPanel.getWidth();
            int panelHeight = scannerPanel.getHeight();
            if (panelWidth <= 0 || panelHeight <= 0) return;

            double previewRatio = (double) scannerPreviewSize.height / (double) scannerPreviewSize.width;
            double panelRatio = (double) panelWidth / (double) panelHeight;
            int surfaceWidth;
            int surfaceHeight;
            if (panelRatio > previewRatio) {
                surfaceWidth = panelWidth;
                surfaceHeight = (int) Math.ceil(panelWidth / previewRatio);
            } else {
                surfaceHeight = panelHeight;
                surfaceWidth = (int) Math.ceil(panelHeight * previewRatio);
            }

            FrameLayout.LayoutParams params = new FrameLayout.LayoutParams(surfaceWidth, surfaceHeight, Gravity.CENTER);
            scannerSurface.setLayoutParams(params);
        });
    }

    private Rect buildCenterCrop(int width, int height) {
        int cropWidth = Math.max(width / 2, Math.min(width, 720));
        int cropHeight = Math.max(height / 2, Math.min(height, 720));
        int left = Math.max(0, (width - cropWidth) / 2);
        int top = Math.max(0, (height - cropHeight) / 2);
        return new Rect(left, top, left + cropWidth, top + cropHeight);
    }

    private Rect buildHorizontalBarcodeCrop(int width, int height) {
        int cropWidth = Math.max(width * 9 / 10, width - 32);
        int cropHeight = Math.max(height / 4, Math.min(height, 260));
        int left = Math.max(0, (width - cropWidth) / 2);
        int top = Math.max(0, (height - cropHeight) / 2);
        return new Rect(left, top, left + cropWidth, top + cropHeight);
    }

    private Rect buildVerticalBarcodeCrop(int width, int height) {
        int cropWidth = Math.max(width / 4, Math.min(width, 260));
        int cropHeight = Math.max(height * 9 / 10, height - 32);
        int left = Math.max(0, (width - cropWidth) / 2);
        int top = Math.max(0, (height - cropHeight) / 2);
        return new Rect(left, top, left + cropWidth, top + cropHeight);
    }

    private void triggerAutoFocus() {
        if (scannerCamera == null) return;
        long now = System.currentTimeMillis();
        if (now - lastAutoFocusAt < 1200) return;
        lastAutoFocusAt = now;
        try {
            scannerCamera.autoFocus((success, camera) -> {
            });
        } catch (Exception ignored) {
        }
    }

    private void decodePreviewFrame(byte[] data, Camera camera) {
        if (!scannerActive || decodingFrame) return;
        decodingFrame = true;
        try {
            Camera.Size size = camera.getParameters().getPreviewSize();
            PlanarYUVLuminanceSource fullSource = new PlanarYUVLuminanceSource(
                    data,
                    size.width,
                    size.height,
                    0,
                    0,
                    size.width,
                    size.height,
                    false
            );
            Result result = decodeSource(fullSource);

            if (result == null) result = decodeCrop(data, size, buildHorizontalBarcodeCrop(size.width, size.height));
            if (result == null) result = decodeCrop(data, size, buildVerticalBarcodeCrop(size.width, size.height));
            if (result == null) result = decodeCrop(data, size, buildCenterCrop(size.width, size.height));
            if (result != null) {
                String text = result.getText();
                runOnUiThread(() -> {
                    stopScanner();
                    if (scannerMode == SCAN_MODE_WEB) {
                        setContentView(webView);
                        dispatchWebScanResult(text);
                        return;
                    }
                    try {
                        String url = normalizeServerUrl(text);
                        prefs.edit().putString(KEY_SERVER_URL, url).apply();
                        Toast.makeText(this, "服务器地址已保存", Toast.LENGTH_SHORT).show();
                        openWeb(url);
                    } catch (IllegalArgumentException ex) {
                        Toast.makeText(this, "二维码不是有效服务器地址", Toast.LENGTH_LONG).show();
                        showServerSetup(text);
                    }
                });
            }
        } catch (Exception ignored) {
            qrReader.reset();
        } finally {
            decodingFrame = false;
        }
    }

    private Result decodeSource(LuminanceSource source) {
        Result result = tryDecode(source);
        if (result != null) return result;
        if (source.isRotateSupported()) {
            return tryDecode(source.rotateCounterClockwise());
        }
        return null;
    }

    private Result decodeCrop(byte[] data, Camera.Size size, Rect crop) {
        PlanarYUVLuminanceSource source = new PlanarYUVLuminanceSource(
                data,
                size.width,
                size.height,
                crop.left,
                crop.top,
                crop.width(),
                crop.height(),
                false
        );
        return decodeSource(source);
    }

    private Result tryDecode(LuminanceSource source) {
        Result result = tryDecodeBitmap(source, false);
        if (result != null) return result;
        return tryDecodeBitmap(source.invert(), true);
    }

    private Result tryDecodeBitmap(LuminanceSource source, boolean inverted) {
        try {
            BinaryBitmap bitmap = new BinaryBitmap(new HybridBinarizer(source));
            return qrReader.decodeWithState(bitmap);
        } catch (Exception ignored) {
            qrReader.reset();
        }

        try {
            BinaryBitmap bitmap = new BinaryBitmap(new GlobalHistogramBinarizer(source));
            return qrReader.decodeWithState(bitmap);
        } catch (Exception ignored) {
            qrReader.reset();
            return null;
        }
    }

    private void stopScanner() {
        scannerActive = false;
        decodingFrame = false;
        if (scannerCamera != null) {
            try {
                scannerCamera.setPreviewCallback(null);
                scannerCamera.stopPreview();
                scannerCamera.release();
            } catch (Exception ignored) {
            }
            scannerCamera = null;
        }
    }

    @Override
    protected void onPause() {
        super.onPause();
        hardwareScanBuffer.setLength(0);
        mainHandler.removeCallbacks(hardwareScanTimeout);
        stopScanner();
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        IntentResult result = IntentIntegrator.parseActivityResult(requestCode, resultCode, data);
        if (result != null) {
            scannerActive = false;
            String text = result.getContents();
            if (text == null || text.trim().isEmpty()) {
                return;
            }
            text = text.trim();
            if (scannerMode == SCAN_MODE_WEB && webView != null) {
                dispatchWebScanResult(text);
                return;
            }
            try {
                String url = normalizeServerUrl(text);
                prefs.edit().putString(KEY_SERVER_URL, url).apply();
                openWeb(url);
            } catch (IllegalArgumentException ex) {
                showServerSetup(text);
                Toast.makeText(this, ex.getMessage(), Toast.LENGTH_LONG).show();
            }
            return;
        }
        super.onActivityResult(requestCode, resultCode, data);
    }

    @Override
    protected void onDestroy() {
        try {
            unregisterReceiver(scanReceiver);
        } catch (Exception ignored) {
        }
        super.onDestroy();
    }

    private void registerScanReceiver() {
        IntentFilter filter = new IntentFilter();
        filter.addAction(ACTION_YODEX_SCAN);
        filter.addAction("com.symbol.datawedge.data");
        filter.addAction("com.symbol.datawedge.api.RESULT_ACTION");
        filter.addAction("com.honeywell.decode.intent.action.BARCODE_DATA");
        filter.addAction("com.nlscan.action.SCANNER_RESULT");
        filter.addAction("com.android.server.scannerservice.broadcast");
        filter.addAction("android.intent.ACTION_DECODE_DATA");
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                registerReceiver(scanReceiver, filter, Context.RECEIVER_EXPORTED);
            } else {
                registerReceiver(scanReceiver, filter);
            }
        } catch (Exception ignored) {
        }
    }

    private String extractScanData(Intent intent) {
        if (intent == null || intent.getExtras() == null) return null;
        String[] keys = new String[] {
                "com.symbol.datawedge.data_string",
                "data",
                "barcode",
                "barocode",
                "scanResult",
                "SCAN_RESULT",
                "SCAN_BARCODE1",
                "scannerdata",
                "barcode_string",
                "decode_data",
                "EXTRA_BARCODE_DECODING_DATA"
        };
        for (String key : keys) {
            Object value = intent.getExtras().get(key);
            String text = stringifyScanExtra(value);
            if (text != null && text.trim().length() >= 3) return text;
        }
        for (String key : intent.getExtras().keySet()) {
            Object value = intent.getExtras().get(key);
            String text = stringifyScanExtra(value);
            if (text != null && text.trim().length() >= 3) return text;
        }
        return null;
    }

    private String stringifyScanExtra(Object value) {
        if (value == null) return null;
        if (value instanceof String) return (String) value;
        if (value instanceof byte[]) return new String((byte[]) value);
        if (value instanceof char[]) return new String((char[]) value);
        return null;
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] grantResults) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == REQ_WEB_CAMERA && pendingPermissionRequest != null) {
            if (grantResults.length > 0 && grantResults[0] == PackageManager.PERMISSION_GRANTED) {
                pendingPermissionRequest.grant(pendingPermissionRequest.getResources());
            } else {
                pendingPermissionRequest.deny();
            }
            pendingPermissionRequest = null;
            return;
        }
        if (requestCode == REQ_SCAN_CAMERA) {
            if (grantResults.length > 0 && grantResults[0] == PackageManager.PERMISSION_GRANTED) {
                showScannerViewV2();
            } else {
                Toast.makeText(this, "没有摄像头权限，无法扫码", Toast.LENGTH_LONG).show();
            }
        }
    }

    @Override
    public boolean dispatchKeyEvent(KeyEvent event) {
        if (captureHardwareScanKey(event)) return true;
        return super.dispatchKeyEvent(event);
    }

    private boolean captureHardwareScanKey(KeyEvent event) {
        if (webView == null || scannerActive || webManualInputActive || event.getAction() != KeyEvent.ACTION_DOWN) return false;
        int keyCode = event.getKeyCode();
        if (keyCode == KeyEvent.KEYCODE_BACK) return false;
        if (event.getRepeatCount() > 0) return true;

        if (keyCode == KeyEvent.KEYCODE_ENTER || keyCode == KeyEvent.KEYCODE_NUMPAD_ENTER || keyCode == KeyEvent.KEYCODE_TAB) {
            if (hardwareScanBuffer.length() > 0) {
                flushHardwareScanBuffer();
                return true;
            }
            return false;
        }

        int unicode = event.getUnicodeChar();
        if (unicode <= 0) return false;
        char value = (char) unicode;
        if (Character.isISOControl(value)) return false;

        hardwareScanBuffer.append(value);
        mainHandler.removeCallbacks(hardwareScanTimeout);
        mainHandler.postDelayed(hardwareScanTimeout, 180);
        return true;
    }

    private void flushHardwareScanBuffer() {
        mainHandler.removeCallbacks(hardwareScanTimeout);
        if (hardwareScanBuffer.length() == 0) return;
        String value = hardwareScanBuffer.toString().trim();
        hardwareScanBuffer.setLength(0);
        if (value.length() < 3) return;
        dispatchHardwareScanResult(value);
    }

    @Override
    public boolean onKeyDown(int keyCode, KeyEvent event) {
        if (keyCode == KeyEvent.KEYCODE_BACK) {
            if (scannerActive) {
                if (scannerMode == SCAN_MODE_WEB && webView != null) {
                    stopScanner();
                    setContentView(webView);
                } else {
                    showServerSetup("");
                }
                return true;
            }
            if (webView != null && webView.canGoBack()) {
                webView.goBack();
                return true;
            }
        }
        return super.onKeyDown(keyCode, event);
    }

    private int dp(int value) {
        return (int) (value * getResources().getDisplayMetrics().density + 0.5f);
    }

    private void dispatchWebScanResult(String value) {
        if (webView == null) return;
        String escaped = value
                .replace("\\", "\\\\")
                .replace("'", "\\'")
                .replace("\n", "\\n")
                .replace("\r", "");
        webView.evaluateJavascript("window.__yodexNativeScanResult && window.__yodexNativeScanResult('" + escaped + "')", null);
    }

    private void dispatchHardwareScanResult(String value) {
        if (webView == null) return;
        String escaped = value
                .replace("\\", "\\\\")
                .replace("'", "\\'")
                .replace("\n", "\\n")
                .replace("\r", "");
        webView.evaluateJavascript("window.__yodexHardwareScanResult && window.__yodexHardwareScanResult('" + escaped + "')", null);
    }

    private class ScannerOverlayView extends View {
        private final Paint maskPaint = new Paint();
        private final Paint framePaint = new Paint();

        ScannerOverlayView(Context context) {
            super(context);
            maskPaint.setColor(Color.argb(80, 255, 255, 255));
            framePaint.setColor(Color.argb(120, 255, 255, 255));
            framePaint.setStyle(Paint.Style.FILL);
        }

        @Override
        protected void onDraw(Canvas canvas) {
            super.onDraw(canvas);
            int width = getWidth();
            int height = getHeight();
            int frameWidth = Math.min(width - dp(48), dp(320));
            int frameHeight = dp(104);
            int left = (width - frameWidth) / 2;
            int top = Math.max(dp(36), (height - frameHeight) / 2 - dp(6));
            int right = left + frameWidth;
            int bottom = top + frameHeight;

            canvas.drawRect(0, 0, width, top, maskPaint);
            canvas.drawRect(0, bottom, width, height, maskPaint);
            canvas.drawRect(0, top, left, bottom, maskPaint);
            canvas.drawRect(right, top, width, bottom, maskPaint);
            canvas.drawRect(left, top, right, bottom, framePaint);
        }
    }

    private class NativeBridge {
        @JavascriptInterface
        public void scanCode() {
            runOnUiThread(() -> startWebScanner());
        }

        @JavascriptInterface
        public void setManualInputActive(boolean active) {
            runOnUiThread(() -> {
                webManualInputActive = active;
                if (active) {
                    hardwareScanBuffer.setLength(0);
                    mainHandler.removeCallbacks(hardwareScanTimeout);
                }
            });
        }
    }
}
