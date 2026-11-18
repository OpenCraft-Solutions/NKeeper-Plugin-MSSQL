import type { SessionViewProps } from '@nkeeper/sdk'
import { dbCore, type DatabaseEngineInfo } from './dbCore'

const SQL_SERVER: DatabaseEngineInfo = {
  product: 'SQL Server',
  databaseLabel: 'Database',
  placeholder: 'SELECT TOP (100) * FROM sys.objects;',
  blockingPermission: 'VIEW SERVER STATE',
}

/** The SQL Server tab: db-core's database tab, told it's SQL Server. */
export function SqlServerView(props: SessionViewProps) {
  const shared = dbCore()
  return shared ? <shared.DatabaseView {...props} engine={SQL_SERVER} /> : null
}
