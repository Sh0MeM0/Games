export type View = 'dashboard' | 'tasks' | 'time' | 'team';
export type TaskStatus = 'todo' | 'in_progress' | 'review' | 'done';
export type Priority = 'low' | 'medium' | 'high';
export interface User {
  id: string;
  name: string;
  email: string;
  role: 'admin' | 'member';
  avatarColor: string;
  title: string;
  active: boolean;
}
export interface Project {
  id: string;
  name: string;
  color: string;
  description: string;
}
export interface Task {
  id: string;
  title: string;
  description: string;
  projectId: string | null;
  parentId: string | null;
  assigneeIds: string[];
  status: TaskStatus;
  priority: Priority;
  progress: number;
  dueDate: string | null;
  createdBy: string;
  createdAt: string;
  updatedAt: string;
}
export interface TimeEntry {
  id: string;
  userId: string;
  taskId: string;
  startAt: string;
  endAt: string;
  durationSeconds: number;
  note: string;
}
export interface Timer {
  id: string;
  userId: string;
  taskId: string;
  startedAt: string;
}
export interface Activity {
  id: string;
  userId: string;
  taskId: string | null;
  text: string;
  createdAt: string;
}
export interface Workspace {
  user: User;
  users: User[];
  projects: Project[];
  tasks: Task[];
  timeEntries: TimeEntry[];
  runningTimer: Timer | null;
  activities: Activity[];
}
export interface ViewProps {
  workspace: Workspace;
  onRefresh: () => Promise<void>;
  onOpenTask: (id: string) => void;
  onCreateTask: (projectId?: string) => void;
}
