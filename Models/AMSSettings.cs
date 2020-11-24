namespace AMSv3Indexer.Models
{
    public class AMSSettings
    {
        public string AccountName { get; set; }
        public string ResourceGroup { get; set; }
        public string ArmEndpoint { get; set; }
        public string AadClientId { get; set; }
        public string AadSecret { get; set; }
        public string SubscriptionId { get; set; }
        public string AadTenantId { get; set; }
    }
}
