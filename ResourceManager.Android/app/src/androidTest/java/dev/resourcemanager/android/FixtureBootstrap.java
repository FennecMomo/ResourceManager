package dev.resourcemanager.android;

public class FixtureBootstrap extends android.content.BroadcastReceiver {
    @Override public void onReceive(android.content.Context context,android.content.Intent intent) {
        android.net.Uri uri=android.provider.DocumentsContract.buildTreeDocumentUri("dev.resourcemanager.android.test.documents","root");
        context.grantUriPermission("dev.resourcemanager.android",uri,android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION | android.content.Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION | android.content.Intent.FLAG_GRANT_PREFIX_URI_PERMISSION);
        setResultCode(1);
    }
}
