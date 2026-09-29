package dev.resourcemanager.android;

import android.content.Intent;
import android.database.Cursor;
import android.database.MatrixCursor;
import android.os.CancellationSignal;
import android.os.ParcelFileDescriptor;
import android.provider.DocumentsContract;
import android.provider.DocumentsProvider;
import java.io.File;
import java.io.FileNotFoundException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;

/** Separate test APK process: uses only platform Java, not the target APK's Kotlin runtime. */
public class FixtureDocumentsProvider extends DocumentsProvider {
    private File root() { return new File(getContext().getFilesDir(), "fixture"); }
    @Override public boolean onCreate() {
        try {
            new File(root(), "child/empty").mkdirs();
            Files.write(new File(root(), "child/中文.txt").toPath(), "SAF fixture 中文 😀".getBytes(StandardCharsets.UTF_8));
            grant(); return true;
        } catch(Exception e) { throw new IllegalStateException(e); }
    }
    private void grant() { getContext().grantUriPermission("dev.resourcemanager.android", DocumentsContract.buildTreeDocumentUri("dev.resourcemanager.android.test.documents", "root"), Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION | Intent.FLAG_GRANT_PREFIX_URI_PERMISSION); }
    private File resolve(String id) {
        File file = id.equals("root") ? root() : new File(root(), id.substring(5));
        try { if(!file.getCanonicalPath().equals(root().getCanonicalPath()) && !file.getCanonicalPath().startsWith(root().getCanonicalPath()+File.separator)) throw new SecurityException(); }
        catch(java.io.IOException e) {throw new IllegalArgumentException(e);} return file;
    }
    private MatrixCursor cursor(String[] projection) { return new MatrixCursor(projection != null ? projection : new String[]{"document_id", "_display_name", "mime_type", "flags", "_size", "last_modified"}); }
    private void add(MatrixCursor cursor, String id) {
        File file=resolve(id); MatrixCursor.RowBuilder row=cursor.newRow();
        for(String column: cursor.getColumnNames()) {
            Object value=null;
            switch(column) {
                case "document_id": value=id;break;
                case "_display_name": value=file.getName();break;
                case "mime_type": value=file.isDirectory() ? DocumentsContract.Document.MIME_TYPE_DIR : "text/plain";break;
                case "flags": value=0;break;
                case "_size": value=file.length();break;
                case "last_modified": value=file.lastModified();break;
            }
            row.add(value);
        }
    }
    @Override public Cursor queryRoots(String[] projection) {return new MatrixCursor(projection != null ? projection : new String[]{"root_id"});}
    @Override public Cursor queryDocument(String id,String[] projection) {MatrixCursor c=cursor(projection);add(c,id);return c;}
    @Override public Cursor queryChildDocuments(String id,String[] projection,String sort) {
        MatrixCursor c=cursor(projection);File[] children=resolve(id).listFiles();
        if(children!=null) for(File file:children) add(c,"root/"+root().toPath().relativize(file.toPath()).toString().replace('\\','/'));return c;
    }
    @Override public ParcelFileDescriptor openDocument(String id,String mode,CancellationSignal signal) throws FileNotFoundException {return ParcelFileDescriptor.open(resolve(id),ParcelFileDescriptor.MODE_READ_ONLY);}
    @Override public boolean isChildDocument(String parent,String child) {return resolve(child).toPath().startsWith(resolve(parent).toPath());}
}
