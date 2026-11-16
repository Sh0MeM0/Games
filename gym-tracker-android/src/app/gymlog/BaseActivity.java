package app.gymlog;

import android.app.Activity;
import android.graphics.drawable.Drawable;
import android.view.Menu;
import android.view.MenuItem;
import android.widget.Toast;

/** Common screen behaviour: background music and its on/off button in the top bar. */
public abstract class BaseActivity extends Activity {
    static final int MENU_MUSIC = 900;

    @Override
    protected void onStart() {
        super.onStart();
        Music.get(this).onScreenStart();
        invalidateOptionsMenu();
    }

    @Override
    protected void onStop() {
        Music.get(this).onScreenStop();
        super.onStop();
    }

    @Override
    public boolean onCreateOptionsMenu(Menu menu) {
        boolean on = Music.get(this).isPlaying();
        MenuItem m = menu.add(0, MENU_MUSIC, 50, on ? "Выключить музыку" : "Включить музыку");
        m.setIcon(whiteIcon(on ? R.drawable.ic_music_on : R.drawable.ic_music_off));
        m.setShowAsAction(MenuItem.SHOW_AS_ACTION_ALWAYS);
        return true;
    }

    @Override
    public boolean onOptionsItemSelected(MenuItem item) {
        if (item.getItemId() == MENU_MUSIC) {
            boolean on = Music.get(this).toggle();
            Toast.makeText(this, on ? "Музыка включена" : "Музыка выключена", Toast.LENGTH_SHORT).show();
            invalidateOptionsMenu();
            return true;
        }
        if (item.getItemId() == android.R.id.home) {
            finish();
            return true;
        }
        return super.onOptionsItemSelected(item);
    }

    Drawable whiteIcon(int res) {
        Drawable d = getDrawable(res);
        if (d != null) {
            d = d.mutate();
            d.setTint(0xFFFFFFFF);
        }
        return d;
    }
}
