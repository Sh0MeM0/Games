import { test, expect, type Page } from '@playwright/test';

async function signIn(page: Page) {
  await page.goto('/');
  await page.getByRole('button', { name: 'Открыть демоверсию' }).click();
  await expect(page.getByRole('heading', { name: /Хорошего дня/ })).toBeVisible();
}

test('desktop: tasks, assigned subtasks, derived progress, timer, manual time and member account', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await signIn(page);
  await page.screenshot({ path: 'test-results/dashboard-desktop.png', fullPage: true });
  await page.getByRole('button', { name: 'Создать задачу', exact: true }).first().click();
  let form = page.getByRole('dialog', { name: 'Новая задача', exact: true });
  await form.getByLabel(/Название задачи/).fill('Клиентский сценарий — запуск проекта');
  await form.getByLabel('Описание', { exact: true }).fill('Проверяем работу команды, исполнителей и учета времени в браузере.');
  await form.getByLabel('Проект', { exact: true }).selectOption('project-1');
  await form.getByLabel('Статус', { exact: true }).selectOption('in_progress');
  await form.getByRole('button', { name: /Анна Кузнецова/ }).click();
  await form.getByRole('button', { name: 'Создать задачу', exact: true }).click();
  await expect(form).toBeHidden();
  await page.getByRole('navigation', { name: 'Главное меню' }).getByRole('button', { name: /^Задачи/ }).click();
  await page.getByRole('button', { name: 'Клиентский сценарий — запуск проекта', exact: true }).click();
  const detail = page.locator('.detail-drawer');
  await expect(detail.getByLabel('Название задачи', { exact: true })).toHaveValue('Клиентский сценарий — запуск проекта');
  await expect(detail.locator('.detail-assignees')).toContainText('Анна Кузнецова');
  for (const [title, status] of [['Согласовать первый этап', 'done'], ['Выполнить второй этап', 'todo']]) {
    await detail.getByRole('button', { name: 'Добавить', exact: true }).click();
    form = page.getByRole('dialog', { name: 'Новая подзадача', exact: true });
    await form.getByLabel(/Название задачи/).fill(title);
    await form.getByLabel('Статус', { exact: true }).selectOption(status);
    await form.getByRole('button', { name: 'Создать подзадачу', exact: true }).click();
    await expect(form).toBeHidden();
  }
  await expect(detail.getByLabel('Прогресс выполнения')).toHaveValue('50');
  await detail.getByRole('button', { name: 'Завершить подзадачу Выполнить второй этап', exact: true }).click();
  await expect(detail.getByLabel('Прогресс выполнения')).toHaveValue('100');
  await detail.getByRole('button', { name: 'Закрыть задачу' }).click();
  await page.getByRole('navigation', { name: 'Главное меню' }).getByRole('button', { name: 'Учёт времени', exact: true }).click();
  await page.getByLabel('Выберите задачу для таймера').selectOption({ label: 'Клиентский сценарий — запуск проекта' });
  await page.getByRole('button', { name: 'Начать работу', exact: true }).click();
  await expect(page.getByText('Таймер запущен', { exact: true })).toBeVisible();
  await page.reload();
  await page.getByRole('navigation', { name: 'Главное меню' }).getByRole('button', { name: 'Учёт времени', exact: true }).click();
  await expect(page.getByText('Таймер запущен', { exact: true })).toBeVisible();
  await page.getByLabel('Примечание к работе').fill('Браузерный тест таймера');
  await page.getByRole('button', { name: 'Остановить', exact: true }).click();
  await expect(page.getByLabel('Выберите задачу для таймера')).toBeVisible();
  await expect(page.getByText('Браузерный тест таймера', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Добавить время', exact: true }).click();
  form = page.getByRole('dialog', { name: 'Добавить время', exact: true });
  await form.getByLabel('Задача', { exact: true }).selectOption({ label: 'Клиентский сценарий — запуск проекта' });
  await form.getByLabel('Часы', { exact: true }).fill('2');
  await form.getByLabel('Минуты', { exact: true }).fill('15');
  await form.getByLabel('Что сделали', { exact: true }).fill('Проверка ручного учета');
  await form.getByRole('button', { name: 'Сохранить время', exact: true }).click();
  await expect(form).toBeHidden();
  const row = page.getByRole('row').filter({ hasText: 'Проверка ручного учета' });
  await expect(row).toContainText('2 ч 15 мин');
  const download = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Экспорт CSV', exact: true }).click();
  expect((await download).suggestedFilename()).toMatch(/potok-time.*\.csv$/);
  await page.getByRole('navigation', { name: 'Главное меню' }).getByRole('button', { name: 'Команда', exact: true }).click();
  await page.getByRole('button', { name: 'Добавить участника', exact: true }).click();
  form = page.getByRole('dialog', { name: 'Добавить участника', exact: true });
  await form.getByLabel('Имя и фамилия', { exact: true }).fill('Тестовый коллега');
  await form.getByLabel('Электронная почта', { exact: true }).fill('browser-test@potok.local');
  await form.getByLabel('Пароль для входа', { exact: true }).fill('BrowserMember2026!');
  await form.getByRole('button', { name: 'Создать учётную запись', exact: true }).click();
  await expect(form).toBeHidden();
  await expect(page.getByRole('row').filter({ hasText: 'browser-test@potok.local' })).toBeVisible();
  await page.getByRole('button', { name: 'Выйти из аккаунта', exact: true }).click();
  await page.getByLabel('Электронная почта').fill('browser-test@potok.local');
  await page.getByLabel('Пароль', { exact: true }).fill('BrowserMember2026!');
  await page.getByRole('button', { name: /Войти в пространство/ }).click();
  await expect(page.getByRole('heading', { name: /Хорошего дня, Тестовый/ })).toBeVisible();
  await page.getByRole('navigation', { name: 'Главное меню' }).getByRole('button', { name: 'Команда', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Добавить участника', exact: true })).toHaveCount(0);
  expect(errors).toEqual([]);
});

test('mobile: adaptive dashboard and menu work without horizontal page overflow', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await signIn(page);
  await page.screenshot({ path: 'test-results/dashboard-mobile.png', fullPage: true });
  const dimensions = await page.evaluate(() => ({ page: document.documentElement.scrollWidth, viewport: window.innerWidth }));
  expect(dimensions.page).toBeLessThanOrEqual(dimensions.viewport + 1);
  await page.getByRole('button', { name: 'Открыть меню', exact: true }).click();
  await page.getByRole('navigation', { name: 'Главное меню' }).getByRole('button', { name: /^Задачи/ }).click();
  await expect(page.getByRole('heading', { name: 'Задачи', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Новая задача', exact: true }).click();
  const form = page.getByRole('dialog', { name: 'Новая задача', exact: true });
  await expect(form.getByLabel(/Название задачи/)).toBeVisible();
  await form.getByRole('button', { name: 'Отмена', exact: true }).click();
  await expect(form).toBeHidden();
});
