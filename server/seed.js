import { randomBytes } from 'node:crypto';
import { hashPassword } from './security.js';

const people = [
  ['Илья Морозов', 'Руководитель команды'], ['Анна Кузнецова', 'Продуктовый дизайнер'],
  ['Дмитрий Соколов', 'Frontend-разработчик'], ['Мария Волкова', 'Менеджер проектов'],
  ['Алексей Попов', 'Backend-разработчик'], ['Екатерина Смирнова', 'Маркетолог'],
  ['Артём Лебедев', 'Разработчик'], ['Полина Новикова', 'Аналитик'],
  ['Максим Орлов', 'QA-инженер'], ['Софья Козлова', 'UX-исследователь'],
  ['Никита Павлов', 'DevOps-инженер'], ['Дарья Романова', 'Редактор'],
  ['Егор Михайлов', 'Разработчик'], ['Алиса Фёдорова', 'Дизайнер'],
  ['Андрей Васильев', 'Менеджер проектов'], ['Виктория Зайцева', 'Аналитик'],
  ['Кирилл Беляев', 'Разработчик'], ['Анастасия Гусева', 'QA-инженер'],
  ['Михаил Титов', 'Разработчик'], ['Елизавета Алексеева', 'Маркетолог'],
  ['Роман Николаев', 'Разработчик'], ['Вероника Сергеева', 'Дизайнер'],
  ['Иван Петров', 'Аналитик'], ['Ксения Андреева', 'Менеджер по работе с клиентами'],
  ['Даниил Макаров', 'Разработчик'], ['Ольга Захарова', 'Бухгалтер'],
  ['Сергей Борисов', 'Специалист поддержки'], ['Валерия Соловьёва', 'Редактор'],
  ['Павел Ефимов', 'QA-инженер'], ['Юлия Егорова', 'HR-менеджер'],
];
const colors = ['#7765D5', '#D68E57', '#599A85', '#7092C4', '#C16D89', '#AB8B52'];
const day = (offset) => {
  const date = new Date();
  date.setUTCHours(0, 0, 0, 0);
  date.setUTCDate(date.getUTCDate() + offset);
  return date;
};
const dateOnly = (offset) => day(offset).toISOString().slice(0, 10);

export function seedDatabase(db) {
  const insertUser = db.prepare('INSERT INTO users (id,name,email,role,avatarColor,title,passwordHash) VALUES (?,?,?,?,?,?,?)');
  // Only the admin has the documented demo password; member passwords are not shared credentials.
  const memberHash = hashPassword(randomBytes(32).toString('base64url'));
  people.forEach(([name, title], i) => insertUser.run(i === 0 ? 'user-admin' : `user-${i + 1}`, name,
    i === 0 ? 'admin@potok.local' : `member${i + 1}@potok.local`, i === 0 ? 'admin' : 'member', colors[i % colors.length], title,
    i === 0 ? hashPassword('PotokDemo2026!') : memberHash));

  const insertProject = db.prepare('INSERT INTO projects (id,name,color,description) VALUES (?,?,?,?)');
  [
    ['Сайт компании', '#7864D9', 'Новый сайт: от исследования до первого посетителя.'],
    ['Мобильное приложение', '#5C9D84', 'Удобный продукт, который всегда под рукой.'],
    ['Маркетинг', '#D39B57', 'Истории, кампании и новые клиенты.'],
    ['Внутренние процессы', '#7092C4', 'Всё, что помогает команде работать вместе.'],
  ].forEach((project, i) => insertProject.run(`project-${i + 1}`, ...project));

  const insertTask = db.prepare(`INSERT INTO tasks
    (id,title,description,projectId,parentId,status,priority,progress,dueDate,createdBy,createdAt,updatedAt)
    VALUES (?,?,?,?,?,?,?,?,?,?,?,?)`);
  const insertAssignee = db.prepare('INSERT INTO task_assignees (taskId,userId) VALUES (?,?)');
  const roots = [
    ['Обновить дизайн главной страницы', 1, 'in_progress', 'high', 55, 3, [1, 2, 3], 'Собрать новую главную страницу: ясная структура, живые кейсы и понятный путь к заявке.'],
    ['Подготовить прототип личного кабинета', 2, 'in_progress', 'high', 65, 5, [2, 5, 10], 'Проверить основные сценарии и подготовить интерактивный прототип для команды разработки.'],
    ['Исследовать конкурентов', 1, 'done', 'medium', 100, -2, [8, 10], 'Изучить шесть близких продуктов. Зафиксировать сильные стороны и возможности для нашего сайта.'],
    ['Согласовать контент для лендинга', 1, 'review', 'medium', 90, 1, [4, 12], 'Финальные тексты для первого экрана, преимуществ, кейсов и FAQ.'],
    ['Настроить push-уведомления', 2, 'todo', 'high', 0, 7, [5, 7], 'Продумать события, реализовать доставку уведомлений и дать пользователю настройки подписки.'],
    ['Запустить рекламную кампанию', 3, 'in_progress', 'high', 40, 2, [6, 20], 'Подготовить креативы и аудитории для запуска кампании нового продукта.'],
    ['Подготовить контент-план на месяц', 3, 'review', 'medium', 85, 2, [6, 12, 28], 'Спланировать темы публикаций, форматы и даты. Свериться с календарём запусков.'],
    ['Обновить базу знаний команды', 4, 'todo', 'low', 0, 10, [1, 4, 30], 'Собрать актуальные инструкции и убрать устаревшие страницы.'],
    ['Проверить адаптивность сайта', 1, 'in_progress', 'medium', 35, -1, [3, 9, 18], 'Проверить основные страницы на мобильных устройствах и исправить критические замечания.'],
    ['Оптимизировать скорость загрузки', 1, 'todo', 'medium', 0, 9, [3, 11], 'Найти узкие места и улучшить ключевые показатели загрузки страниц.'],
    ['Спроектировать экран статистики', 2, 'todo', 'medium', 0, 6, [2, 14], 'Показать главное за неделю: активность, результаты и следующий полезный шаг.'],
    ['Провести интервью с пользователями', 2, 'done', 'medium', 100, -3, [8, 10], 'Пять интервью с активными пользователями. Обобщить наблюдения и передать выводы команде.'],
    ['Подготовить еженедельный отчёт', 3, 'done', 'low', 100, -1, [6, 16], 'Собрать результаты кампаний и коротко объяснить изменения ключевых метрик.'],
    ['Разработать программу адаптации', 4, 'in_progress', 'medium', 60, 8, [15, 30], 'Первые две недели нового коллеги: знакомства, материалы и небольшие задачи.'],
    ['Согласовать план следующего спринта', 4, 'review', 'high', 95, 0, [1, 4, 15], 'Сверить приоритеты, загрузку команды и цели на следующие две недели.'],
    ['Настроить автоматические резервные копии', 4, 'done', 'high', 100, -4, [5, 11], 'Настроить ежедневное копирование и проверить восстановление из резервной копии.'],
  ];
  const createdAt = day(-12).toISOString();
  const updatedAt = new Date(Date.now() - 45 * 60 * 1000).toISOString();
  const add = (id, title, description, projectId, parentId, status, priority, progress, dueDate, assignees) => {
    insertTask.run(id, title, description, projectId, parentId, status, priority, progress, dueDate, 'user-admin', createdAt, updatedAt);
    assignees.forEach((n) => insertAssignee.run(id, n === 1 ? 'user-admin' : `user-${n}`));
  };
  roots.forEach(([title, project, status, priority, progress, due, assignees, description], i) =>
    add(`task-${i + 1}`, title, description, `project-${project}`, null, status, priority, progress, dateOnly(due), assignees));

  const children = [
    [1, 'Разработать первый экран', 'in_progress', 70, [2, 3]],
    [1, 'Собрать блок с кейсами', 'in_progress', 40, [1, 2, 12]],
    [2, 'Описать пользовательские сценарии', 'done', 100, [10]],
    [2, 'Собрать интерактивный прототип', 'in_progress', 30, [2]],
    [3, 'Собрать таблицу сравнения', 'done', 100, [8]],
    [3, 'Подготовить выводы для команды', 'done', 100, [10]],
    [4, 'Проверить тон и формулировки', 'review', 90, [12]],
    [5, 'Подготовить схему событий', 'todo', 0, [5]],
    [5, 'Добавить настройки подписки', 'todo', 0, [7]],
    [6, 'Подготовить рекламные креативы', 'in_progress', 80, [6, 14]],
    [6, 'Настроить аудитории и аналитику', 'todo', 0, [20]],
    [8, 'Собрать инструкции для новичков', 'todo', 0, [4, 30]],
  ];
  children.forEach(([parent, title, status, progress, assignees], i) => {
    const root = roots[parent - 1];
    add(`subtask-${i + 1}`, title, 'Результат и материалы сохраняем в описании задачи. После завершения передаём на проверку.',
      `project-${root[1]}`, `task-${parent}`, status, root[3], progress, dateOnly(root[5]), assignees);
  });

  const insertTime = db.prepare('INSERT INTO time_entries (id,userId,taskId,startAt,endAt,durationSeconds,note) VALUES (?,?,?,?,?,?,?)');
  const notes = ['Работа над макетами', 'Реализация и проверка', 'Обсуждение решений с командой', 'Подготовка материалов', 'Основная работа по задаче'];
  let timeId = 0;
  for (let offset = -13; offset <= 0; offset++) {
    if ([0, 6].includes(day(offset).getUTCDay())) continue;
    for (let i = 0; i < 7; i++) {
      const userNumber = i === 0 ? 1 : 2 + ((i + offset + 26) % 14);
      const rootIndex = (i + offset + 32) % roots.length;
      const start = day(offset);
      start.setUTCHours(7 + i % 4, 15);
      if (start.getTime() > Date.now()) continue;
      const durationSeconds = (45 + ((i * 17 + offset + 40) % 100)) * 60;
      const end = new Date(Math.min(Date.now(), start.getTime() + durationSeconds * 1000));
      insertTime.run(`time-${++timeId}`, userNumber === 1 ? 'user-admin' : `user-${userNumber}`, `task-${rootIndex + 1}`,
        start.toISOString(), end.toISOString(), Math.floor((end - start) / 1000), notes[i % notes.length]);
    }
  }
  const insertActivity = db.prepare('INSERT INTO activities (id,userId,taskId,text,createdAt) VALUES (?,?,?,?,?)');
  [
    ['user-2', 'task-1', 'обновила прогресс задачи «Обновить дизайн главной страницы»', 35],
    ['user-3', 'task-9', 'добавил запись рабочего времени в задаче «Проверить адаптивность сайта»', 72],
    ['user-4', 'task-15', 'передала на проверку план следующего спринта', 118],
    ['user-6', 'task-7', 'обновила контент-план на месяц', 190],
    ['user-admin', 'task-8', 'создал задачу «Обновить базу знаний команды»', 250],
    ['user-10', 'task-12', 'завершила интервью с пользователями', 420],
    ['user-11', 'task-16', 'завершил настройку резервных копий', 660],
    ['user-12', 'task-4', 'отправила тексты лендинга на проверку', 810],
  ].forEach(([userId, taskId, text, minutes], i) => insertActivity.run(`activity-${i + 1}`, userId, taskId, text,
    new Date(Date.now() - minutes * 60000).toISOString()));
}
