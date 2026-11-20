package app.gymlog;

import android.app.Activity;
import android.graphics.drawable.Drawable;
import android.view.MenuItem;

/** Common screen behaviour: the "back" arrow in the top bar closes the screen. */
public abstract class BaseActivity extends Activity {

    @Override
    public boolean onOptionsItemSelected(MenuItem item) {
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
