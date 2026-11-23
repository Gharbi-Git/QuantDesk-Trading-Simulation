export function roleLabel(role: string): string {
  switch (role) {
    case 'SUPERUSER': return 'Admin'
    case 'ADMIN': return 'Session Organizer'
    case 'PLAYER': return 'Player'
    default: return role
  }
}
