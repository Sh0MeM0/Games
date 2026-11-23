package app.gymlog;

import android.app.Application;

/** Loads the optional personal catalog before any screen opens. */
public class GymApp extends Application {
    @Override
    public void onCreate() {
        super.onCreate();
        Extras.load(this);
    }
}
